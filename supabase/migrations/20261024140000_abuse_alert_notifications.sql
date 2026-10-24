-- Discord notifications for license-abuse alerts. The daily scan
-- (flag_license_abuse) now calls notify_abuse_alerts(), which POSTs a Discord
-- message for each open, not-yet-notified alert via pg_net. The webhook URL is a
-- secret and is NOT stored in source control: set it on the live project with
--   update public.app_webhooks set discord_url = '<webhook url>' where id = 1;

create extension if not exists pg_net;

create table if not exists public.app_webhooks (
  id int primary key default 1,
  discord_url text,
  updated_at timestamptz not null default now(),
  constraint app_webhooks_single_row check (id = 1)
);
alter table public.app_webhooks enable row level security;
revoke all on public.app_webhooks from anon, authenticated;
insert into public.app_webhooks (id) values (1) on conflict (id) do nothing;

alter table public.license_abuse_alerts add column if not exists notified_at timestamptz;

create or replace function public.notify_abuse_alerts()
returns int
language plpgsql
security definer
set search_path = public, extensions, net
as $$
declare
  v_url text;
  v_count int := 0;
  r record;
begin
  select discord_url into v_url from public.app_webhooks where id = 1;
  if v_url is null or v_url = '' then return 0; end if;

  for r in
    select a.id, a.license_id, a.device_count, a.max_devices, l.key_hint
    from public.license_abuse_alerts a
    join public.licenses l on l.id = a.license_id
    where a.resolved_at is null and a.notified_at is null
  loop
    perform net.http_post(
      url := v_url,
      body := jsonb_build_object('content', format(
        E':warning: **DANBOT license abuse**\nKey `%s` (license `%s`) is active on **%s** devices, allowed **%s**.\nTrace a leaked build with trace_tuning(), or burn_tuning(blob) to revoke the watermark.',
        coalesce(r.key_hint, '?'), r.license_id, r.device_count, r.max_devices)),
      headers := '{"Content-Type": "application/json"}'::jsonb);
    update public.license_abuse_alerts set notified_at = now(), updated_at = now() where id = r.id;
    v_count := v_count + 1;
  end loop;
  return v_count;
end;
$$;
revoke all on function public.notify_abuse_alerts() from anon, authenticated;

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

  perform public.notify_abuse_alerts();
  return v_raised;
end;
$$;
revoke all on function public.flag_license_abuse() from anon, authenticated;
