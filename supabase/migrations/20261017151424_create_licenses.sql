-- License keys for DANBOT. Only the SHA-256 of a key is stored; the plain key
-- is shown once by license_create(). Clients never touch these tables: the
-- "license" edge function calls the functions below with the service role.

create table public.licenses (
  id uuid primary key default gen_random_uuid(),
  key_hash text not null unique,
  key_hint text not null,
  duration_days int check (duration_days is null or duration_days > 0),
  max_devices int not null default 1 check (max_devices between 1 and 100),
  note text not null default '',
  created_at timestamptz not null default now(),
  redeemed_by uuid references auth.users(id) on delete set null,
  redeemed_at timestamptz,
  expires_at timestamptz,
  revoked_at timestamptz
);
comment on table public.licenses is 'DANBOT license keys (hashed). duration_days null = lifetime; the clock starts at redemption.';
create index licenses_redeemed_by_idx on public.licenses (redeemed_by);

create table public.license_devices (
  license_id uuid not null references public.licenses(id) on delete cascade,
  device_id text not null check (device_id ~ '^[0-9a-f]{64}$'),
  device_name text not null default '',
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  primary key (license_id, device_id)
);
comment on table public.license_devices is 'PCs registered against a license, up to licenses.max_devices.';

alter table public.licenses enable row level security;
alter table public.license_devices enable row level security;
revoke all on public.licenses, public.license_devices from anon, authenticated;

-- Strips the DANBOT- prefix, dashes and spaces; reads O as 0 and I/L as 1.
create function public.license_normalize_key(p_key text) returns text
language sql immutable set search_path = '' as $$
  select translate(
    regexp_replace(regexp_replace(upper(coalesce(p_key, '')), '^\s*DANBOT', ''), '[^0-9A-Z]', '', 'g'),
    'OIL', '011')
$$;

-- Admin: run in the SQL editor. Returns the plain keys once; store them yourself.
create function public.license_create(
  p_count int default 1,
  p_duration_days int default null,
  p_max_devices int default 1,
  p_note text default '')
returns table (license_key text, days int, devices int, label text)
language plpgsql set search_path = '' as $$
declare
  alphabet constant text := '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  raw bytea;
  body text;
begin
  if p_count is null or p_count < 1 or p_count > 500 then
    raise exception 'p_count must be between 1 and 500';
  end if;
  for n in 1..p_count loop
    raw := extensions.gen_random_bytes(20);
    body := '';
    for i in 0..19 loop
      body := body || substr(alphabet, (get_byte(raw, i) & 31) + 1, 1);
    end loop;
    insert into public.licenses (key_hash, key_hint, duration_days, max_devices, note)
    values (encode(extensions.digest(body, 'sha256'), 'hex'), right(body, 4), p_duration_days, p_max_devices,
            coalesce(p_note, ''));
    license_key := 'DANBOT-' || substr(body, 1, 5) || '-' || substr(body, 6, 5) || '-' || substr(body, 11, 5) || '-' ||
                   substr(body, 16, 5);
    days := p_duration_days;
    devices := p_max_devices;
    label := coalesce(p_note, '');
    return next;
  end loop;
end
$$;

-- The account's best active license, registering this PC if a slot is free.
create function public.license_check(p_user uuid, p_device_id text, p_device_name text) returns jsonb
language plpgsql set search_path = '' as $$
declare
  lic public.licenses;
  used int;
  state text;
begin
  if p_device_id is null or p_device_id !~ '^[0-9a-f]{64}$' then
    return jsonb_build_object('error', 'Invalid device id.');
  end if;

  select * into lic from public.licenses
   where redeemed_by = p_user and revoked_at is null and (expires_at is null or expires_at > now())
   order by expires_at desc nulls first, redeemed_at desc
   limit 1
   for update;

  if not found then
    select * into lic from public.licenses
     where redeemed_by = p_user
     order by coalesce(revoked_at, expires_at) desc nulls last
     limit 1;
    state := case when not found then 'none' when lic.revoked_at is not null then 'revoked' else 'expired' end;
    return jsonb_build_object('status', state, 'license_id', lic.id, 'expires_at', lic.expires_at,
      'max_devices', coalesce(lic.max_devices, 0), 'devices_used', 0, 'key_hint', coalesce(lic.key_hint, ''));
  end if;

  update public.license_devices
     set last_seen = now(), device_name = left(coalesce(p_device_name, ''), 64)
   where license_id = lic.id and device_id = p_device_id;
  if not found then
    select count(*) into used from public.license_devices where license_id = lic.id;
    if used >= lic.max_devices then
      return jsonb_build_object('status', 'device_limit', 'license_id', lic.id, 'expires_at', lic.expires_at,
        'max_devices', lic.max_devices, 'devices_used', used, 'key_hint', lic.key_hint);
    end if;
    insert into public.license_devices (license_id, device_id, device_name)
    values (lic.id, p_device_id, left(coalesce(p_device_name, ''), 64));
  end if;

  select count(*) into used from public.license_devices where license_id = lic.id;
  return jsonb_build_object('status', 'active', 'license_id', lic.id, 'expires_at', lic.expires_at,
    'max_devices', lic.max_devices, 'devices_used', used, 'key_hint', lic.key_hint);
end
$$;

-- Ties an unused key to the account (starting its clock), then checks as above.
create function public.license_redeem(p_user uuid, p_key text, p_device_id text, p_device_name text) returns jsonb
language plpgsql set search_path = '' as $$
declare
  lic public.licenses;
  body text := public.license_normalize_key(p_key);
begin
  if length(body) <> 20 then
    return jsonb_build_object('error', 'That is not a valid license key.');
  end if;
  select * into lic from public.licenses where key_hash = encode(extensions.digest(body, 'sha256'), 'hex') for update;
  if not found then
    return jsonb_build_object('error', 'License key not found.');
  end if;
  if lic.revoked_at is not null then
    return jsonb_build_object('error', 'This license key has been revoked.');
  end if;
  if lic.redeemed_by is not null and lic.redeemed_by <> p_user then
    return jsonb_build_object('error', 'This license key is already used by another account.');
  end if;
  if lic.redeemed_by is null then
    update public.licenses
       set redeemed_by = p_user,
           redeemed_at = now(),
           expires_at = case when duration_days is null then null else now() + make_interval(days => duration_days) end
     where id = lic.id;
  end if;
  return public.license_check(p_user, p_device_id, p_device_name);
end
$$;

-- Admin helpers.
create function public.license_revoke(p_license_id uuid) returns void
language sql set search_path = '' as $$
  update public.licenses set revoked_at = now() where id = p_license_id and revoked_at is null
$$;

create function public.license_reset_devices(p_license_id uuid) returns int
language sql set search_path = '' as $$
  with gone as (delete from public.license_devices where license_id = p_license_id returning 1)
  select count(*)::int from gone
$$;

create view public.license_overview with (security_invoker = true) as
select l.id, l.key_hint, l.note, u.email, l.duration_days, l.redeemed_at, l.expires_at, l.revoked_at,
       l.max_devices, (select count(*) from public.license_devices d where d.license_id = l.id) as devices_used,
       case when l.revoked_at is not null then 'revoked'
            when l.redeemed_by is null then 'unused'
            when l.expires_at is not null and l.expires_at <= now() then 'expired'
            else 'active' end as status,
       l.created_at
  from public.licenses l
  left join auth.users u on u.id = l.redeemed_by;
revoke all on public.license_overview from anon, authenticated;

-- ECDSA P-256 key that signs license tokens, kept in Vault. The edge function
-- creates it on first use, so the private half never leaves Supabase.
create function public.license_signing_key() returns jsonb
language sql stable security definer set search_path = '' as $$
  select jsonb_build_object(
    'private', (select decrypted_secret from vault.decrypted_secrets where name = 'license_signing_private'),
    'public', (select decrypted_secret from vault.decrypted_secrets where name = 'license_signing_public'))
  where exists (select 1 from vault.secrets where name = 'license_signing_private')
$$;

create function public.license_init_signing_key(p_private text, p_public text) returns jsonb
language plpgsql security definer set search_path = '' as $$
begin
  if not exists (select 1 from vault.secrets where name = 'license_signing_private') then
    begin
      perform vault.create_secret(p_private, 'license_signing_private', 'DANBOT license token signing key (PKCS#8)');
      perform vault.create_secret(p_public, 'license_signing_public', 'DANBOT license token public key (SPKI)');
    exception when unique_violation then
      null; -- another instance won the race; use its key
    end;
  end if;
  return public.license_signing_key();
end
$$;

revoke all on function
  public.license_normalize_key(text),
  public.license_create(int, int, int, text),
  public.license_check(uuid, text, text),
  public.license_redeem(uuid, text, text, text),
  public.license_revoke(uuid),
  public.license_reset_devices(uuid),
  public.license_signing_key(),
  public.license_init_signing_key(text, text)
from public, anon, authenticated;

grant execute on function
  public.license_normalize_key(text),
  public.license_check(uuid, text, text),
  public.license_redeem(uuid, text, text, text),
  public.license_signing_key(),
  public.license_init_signing_key(text, text)
to service_role;
grant select, insert, update, delete on public.licenses, public.license_devices to service_role;
