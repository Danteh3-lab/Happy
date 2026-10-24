-- Per-license watermarking for the gated tuning. Each active grant receives a
-- blob whose numeric detection values are nudged by tiny, deterministic amounts
-- (well within detection tolerance, so the bot behaves identically) that are
-- unique per license and per epoch. A leaked hardcoded value set can then be
-- traced back to the license that produced it with trace_tuning(). Rotating the
-- epoch (update app_tuning_secret set epoch = epoch + 1) re-watermarks everyone,
-- invalidating previously leaked sets.

create table public.app_tuning_secret (
  id int primary key default 1,
  secret bytea not null default extensions.gen_random_bytes(32),
  epoch int not null default 1,
  updated_at timestamptz not null default now(),
  constraint app_tuning_secret_single_row check (id = 1)
);
comment on table public.app_tuning_secret is 'Server-only HMAC secret and rotation epoch for per-license tuning watermarks.';
alter table public.app_tuning_secret enable row level security;
revoke all on public.app_tuning_secret from anon, authenticated;
insert into public.app_tuning_secret (id) values (1);

-- Deterministic signed jitter in [-radius, radius] for one field of one license.
create or replace function public._tuning_jitter(
  p_license uuid, p_epoch int, p_secret bytea, p_path text, p_radius int)
returns int
language sql
immutable
as $$
  select (get_byte(
            extensions.hmac(
              convert_to(p_license::text || ':' || p_epoch || ':' || p_path, 'UTF8'),
              p_secret, 'sha256'), 0) % (2 * p_radius + 1)) - p_radius;
$$;

-- Builds the watermarked blob for a license. Null license returns the base blob.
create or replace function public.license_tuning(p_license_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = public, extensions
as $$
declare
  v_secret bytea;
  v_epoch  int;
  v_base   jsonb;
  v_anchor jsonb;
  v_parry  jsonb;
  v_flash  jsonb;
begin
  select blob into v_base from public.app_tuning where id = 1;
  if v_base is null then return null; end if;
  if p_license_id is null then return v_base; end if;
  select secret, epoch into v_secret, v_epoch from public.app_tuning_secret where id = 1;

  v_anchor := v_base->'anchor';
  v_anchor := jsonb_set(v_anchor, '{gr}',     to_jsonb(least(greatest((v_anchor->>'gr')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.gr',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{gg}',     to_jsonb(least(greatest((v_anchor->>'gg')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.gg',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{gb}',     to_jsonb(least(greatest((v_anchor->>'gb')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.gb',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{yr}',     to_jsonb(least(greatest((v_anchor->>'yr')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.yr',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{yg}',     to_jsonb(least(greatest((v_anchor->>'yg')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.yg',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{yb}',     to_jsonb(least(greatest((v_anchor->>'yb')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.yb',2),   0),255)));
  v_anchor := jsonb_set(v_anchor, '{radius}', to_jsonb(greatest((v_anchor->>'radius')::int        + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.radius',3), 1)));
  v_anchor := jsonb_set(v_anchor, '{maxpx}',  to_jsonb(greatest((v_anchor->>'maxpx')::int         + _tuning_jitter(p_license_id,v_epoch,v_secret,'anchor.maxpx',16),1)));

  v_parry := v_base->'parry';
  v_parry := jsonb_set(v_parry, '{start}',   to_jsonb(greatest((v_parry->>'start')::int   + _tuning_jitter(p_license_id,v_epoch,v_secret,'parry.start',3),   0)));
  v_parry := jsonb_set(v_parry, '{end}',     to_jsonb(greatest((v_parry->>'end')::int     + _tuning_jitter(p_license_id,v_epoch,v_secret,'parry.end',3),     1)));
  v_parry := jsonb_set(v_parry, '{minbase}', to_jsonb(greatest((v_parry->>'minbase')::int + _tuning_jitter(p_license_id,v_epoch,v_secret,'parry.minbase',5), 1)));
  v_parry := jsonb_set(v_parry, '{delta}',   to_jsonb(greatest((v_parry->>'delta')::int   + _tuning_jitter(p_license_id,v_epoch,v_secret,'parry.delta',3),   1)));

  v_flash := v_base->'flash';
  v_flash := jsonb_set(v_flash, '{tol}', to_jsonb(greatest((v_flash->>'tol')::int + _tuning_jitter(p_license_id,v_epoch,v_secret,'flash.tol',1), 1)));
  v_flash := jsonb_set(v_flash, '{r}',   to_jsonb(least(greatest((v_flash->>'r')::int + _tuning_jitter(p_license_id,v_epoch,v_secret,'flash.r',2), 0),255)));
  v_flash := jsonb_set(v_flash, '{g}',   to_jsonb(least(greatest((v_flash->>'g')::int + _tuning_jitter(p_license_id,v_epoch,v_secret,'flash.g',2), 0),255)));
  v_flash := jsonb_set(v_flash, '{b}',   to_jsonb(least(greatest((v_flash->>'b')::int + _tuning_jitter(p_license_id,v_epoch,v_secret,'flash.b',2), 0),255)));

  return jsonb_set(jsonb_set(jsonb_set(v_base, '{anchor}', v_anchor), '{parry}', v_parry), '{flash}', v_flash);
end;
$$;

revoke all on function public.license_tuning(uuid) from anon, authenticated;

-- Given a leaked blob, rank licenses by how many watermarked fields match.
-- An exact or near-exact top row names the account the values came from.
create or replace function public.trace_tuning(p_blob jsonb)
returns table(license_id uuid, key_hint text, matched int, total int)
language plpgsql
security definer
set search_path = public, extensions
as $$
declare
  v_paths text[] := array[
    'anchor.gr','anchor.gg','anchor.gb','anchor.yr','anchor.yg','anchor.yb','anchor.radius','anchor.maxpx',
    'parry.start','parry.end','parry.minbase','parry.delta',
    'flash.tol','flash.r','flash.g','flash.b'];
begin
  return query
  with expected as (
    select l.id, l.key_hint, public.license_tuning(l.id) as blob
    from public.licenses l
  )
  select e.id, e.key_hint,
         (select count(*)::int from unnest(v_paths) p
            where (e.blob #>> string_to_array(p,'.')) is not distinct from (p_blob #>> string_to_array(p,'.'))) as matched,
         array_length(v_paths,1) as total
  from expected e
  order by matched desc, e.id;
end;
$$;

revoke all on function public.trace_tuning(jsonb) from anon, authenticated;
