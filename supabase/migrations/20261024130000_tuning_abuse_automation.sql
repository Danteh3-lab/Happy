-- Automated leak defence for the gated tuning:
--  * license_abuse_alerts: one open row per license whose device fan-out exceeds
--    its allowance (the real-world crack/sharing signal).
--  * flag_license_abuse(): scan that raises/updates those alerts. Scheduled daily.
--  * rotate_tuning_epoch(): bumps the watermark epoch, invalidating every
--    previously leaked value set. Scheduled monthly, and called by burn_tuning().
--  * burn_tuning(blob): trace a leaked blob to its license and rotate in one call.
--  * tuning_epoch_rotations: audit log of every rotation and why.

create table public.tuning_epoch_rotations (
  id bigint generated always as identity primary key,
  epoch int not null,
  reason text not null,
  traced_license uuid,
  created_at timestamptz not null default now()
);
alter table public.tuning_epoch_rotations enable row level security;
revoke all on public.tuning_epoch_rotations from anon, authenticated;

create table public.license_abuse_alerts (
  id bigint generated always as identity primary key,
  license_id uuid not null references public.licenses(id) on delete cascade,
  kind text not null default 'device_fanout',
  device_count int not null,
  max_devices int not null,
  detail jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  resolved_at timestamptz
);
create unique index license_abuse_open_uniq
  on public.license_abuse_alerts (license_id, kind) where resolved_at is null;
alter table public.license_abuse_alerts enable row level security;
revoke all on public.license_abuse_alerts from anon, authenticated;

-- Raise or refresh an open alert for every non-revoked license using more
-- distinct devices than it is allowed. Existing open alerts are updated in place;
-- licenses that fall back within allowance have their open alert auto-resolved.
create or replace function public.flag_license_abuse()
returns int
language plpgsql
security definer
set search_path = public, extensions
as $$
declare
  v_raised int := 0;
begin
  with usage as (
    select l.id as license_id,
           greatest(l.max_devices, 0) as max_devices,
           count(distinct d.device_id) as device_count
    from public.licenses l
    left join public.license_devices d on d.license_id = l.id
    where l.revoked_at is null
    group by l.id, l.max_devices
  ),
  over_limit as (
    select * from usage where max_devices > 0 and device_count > max_devices
  ),
  upsert as (
    insert into public.license_abuse_alerts (license_id, kind, device_count, max_devices, detail)
    select license_id, 'device_fanout', device_count, max_devices,
           jsonb_build_object('over_by', device_count - max_devices)
    from over_limit
    on conflict (license_id, kind) where resolved_at is null
    do update set device_count = excluded.device_count,
                  max_devices  = excluded.max_devices,
                  detail       = excluded.detail,
                  updated_at   = now()
    returning 1
  )
  select count(*) into v_raised from upsert;

  update public.license_abuse_alerts a
     set resolved_at = now(), updated_at = now()
   where a.resolved_at is null
     and not exists (
       select 1 from public.licenses l
       left join public.license_devices d on d.license_id = l.id
       where l.id = a.license_id and l.revoked_at is null
       group by l.id, l.max_devices
       having l.max_devices > 0 and count(distinct d.device_id) > l.max_devices);

  return v_raised;
end;
$$;
revoke all on function public.flag_license_abuse() from anon, authenticated;

create or replace function public.rotate_tuning_epoch(p_reason text default 'manual', p_traced uuid default null)
returns int
language plpgsql
security definer
set search_path = public, extensions
as $$
declare
  v_epoch int;
begin
  update public.app_tuning_secret set epoch = epoch + 1, updated_at = now() where id = 1
    returning epoch into v_epoch;
  insert into public.tuning_epoch_rotations (epoch, reason, traced_license)
    values (v_epoch, p_reason, p_traced);
  return v_epoch;
end;
$$;
revoke all on function public.rotate_tuning_epoch(text, uuid) from anon, authenticated;

create or replace function public.burn_tuning(p_blob jsonb)
returns table(traced_license uuid, key_hint text, matched int, total int, new_epoch int)
language plpgsql
security definer
set search_path = public, extensions
as $$
declare
  v_lic uuid; v_hint text; v_matched int; v_total int; v_epoch int;
begin
  select t.license_id, t.key_hint, t.matched, t.total
    into v_lic, v_hint, v_matched, v_total
    from public.trace_tuning(p_blob) t
    order by t.matched desc limit 1;
  v_epoch := public.rotate_tuning_epoch('burn: leaked blob traced', v_lic);
  return query select v_lic, v_hint, v_matched, v_total, v_epoch;
end;
$$;
revoke all on function public.burn_tuning(jsonb) from anon, authenticated;

-- Schedules (pg_cron): daily sharing scan, monthly epoch rotation.
create extension if not exists pg_cron;
select cron.unschedule(jobid) from cron.job where jobname in ('flag-license-abuse','rotate-tuning-epoch');
select cron.schedule('flag-license-abuse', '0 3 * * *', $$select public.flag_license_abuse();$$);
select cron.schedule('rotate-tuning-epoch', '0 0 1 * *', $$select public.rotate_tuning_epoch('scheduled monthly rotation');$$);
