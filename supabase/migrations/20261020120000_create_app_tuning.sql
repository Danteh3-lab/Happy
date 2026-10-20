-- Gated detection tuning for DANBOT. The hand-tuned anchor/flash colours and
-- parry thresholds that make the bot see anything live here, server-side, so
-- they are not compiled into the shipped exe. The "license" edge function
-- returns the blob only for an active grant; clients cache it (DPAPI) for the
-- offline window. Clients never read this table directly: the edge function
-- uses the service role, and anon/authenticated are revoked below.
--
-- The blob schema (version 1) matches GatedTuning in the app:
--   { "v":1,
--     "anchor": { gr,gg,gb, yr,yg,yb, radius, maxpx },
--     "parry":  { start, end, minbase, delta },
--     "flash":  { tol, livetol, livemin, r, g, b } }

create table public.app_tuning (
  id int primary key default 1,
  version int not null default 1,
  blob jsonb not null,
  updated_at timestamptz not null default now(),
  constraint app_tuning_single_row check (id = 1)
);
comment on table public.app_tuning is 'Gated DANBOT detection tuning, served to active grants by the license edge function.';

alter table public.app_tuning enable row level security;
revoke all on public.app_tuning from anon, authenticated;
grant select on public.app_tuning to service_role;

-- Seed with the values that shipped as constants before this change.
insert into public.app_tuning (id, version, blob) values (
  1, 1,
  jsonb_build_object(
    'v', 1,
    'anchor', jsonb_build_object('gr', 5, 'gg', 131, 'gb', 65, 'yr', 255, 'yg', 255, 'yb', 10, 'radius', 64, 'maxpx', 4096),
    'parry',  jsonb_build_object('start', 150, 'end', 650, 'minbase', 400, 'delta', 250),
    'flash',  jsonb_build_object('tol', 24, 'livetol', 2, 'livemin', 3, 'r', 255, 'g', 154, 'b', 141)
  )
);
