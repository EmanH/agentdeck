// Receives Airwallex webhooks, keeps a short queue of "new payment / new subscriber" summaries in KV,
// and serves them plus MRR / active-subscriber stats to AgentDeck on the PC. The PC polls; nothing reaches it inbound.

const TTL = 30 * 24 * 3600;
const TOLERANCE_MS = 5 * 60 * 1000;
const STATS_CACHE_S = 300;

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/webhook") return await webhook(req, env);
      if (req.method === "POST" && url.pathname === "/test") {
        if (!authorized(req, env)) return new Response("unauthorized", { status: 401 });
        const id = `test_${Date.now()}`;
        const key = `evt:${String(Date.now()).padStart(15, "0")}:${id}`;
        const fake = { key, kind: "payment", ref: id, label: "test@example.com", amount: 49, currency: "AUD", at: Date.now(),
          url: env.PAYMENT_URL.replace("{id}", id), country: "NZ", customer: "Test Customer", email: "test@example.com",
          method: "VISA •••• 4242", cardCountry: "NZ", funding: "credit", description: "Pro plan (test)", orderId: "order-test", risk: 12 };
        await env.EVENTS.put(key, JSON.stringify(fake), { expirationTtl: 3600 });
        return Response.json(fake);
      }
      if (req.method === "GET" && (url.pathname === "/events" || url.pathname === "/stats")) {
        if (!authorized(req, env)) return new Response("unauthorized", { status: 401 });
        return url.pathname === "/events" ? await events(url, env) : await stats(env, url.searchParams.has("fresh"));
      }
      return new Response("not found", { status: 404 });
    } catch (e) {
      console.error(e);
      return new Response("error", { status: 500 });
    }
  },
};

function authorized(req, env) {
  const got = req.headers.get("authorization") ?? "";
  return !!env.RELAY_TOKEN && timingSafeEqual(got, `Bearer ${env.RELAY_TOKEN}`);
}

function timingSafeEqual(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function hmacHex(secret, message) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message));
  return [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, "0")).join("");
}

async function webhook(req, env) {
  const body = await req.text(); // raw body: the signature covers it byte for byte
  const ts = req.headers.get("x-timestamp") ?? "";
  const sig = (req.headers.get("x-signature") ?? "").toLowerCase();
  if (!env.WEBHOOK_SECRET || !ts || !sig) return new Response("missing signature", { status: 401 });
  if (Math.abs(Date.now() - Number(ts)) > TOLERANCE_MS) return new Response("stale", { status: 401 });
  if (!timingSafeEqual(await hmacHex(env.WEBHOOK_SECRET, ts + body), sig)) return new Response("bad signature", { status: 401 });

  const evt = JSON.parse(body);
  const summary = summarize(evt, env);
  // Airwallex retries deliveries, so dedupe on the event id.
  // One "checkout" card per intent, however many created/pending events it sends.
  const dedupe = summary?.kind === "intent" ? `seen:intent:${summary.ref}` : `seen:${evt.id}`;
  if (summary && !(await env.EVENTS.get(dedupe))) {
    const key = `evt:${String(Date.now()).padStart(15, "0")}:${evt.id}`; // sorts by arrival
    await env.EVENTS.put(key, JSON.stringify({ ...summary, key }), { expirationTtl: TTL });
    await env.EVENTS.put(dedupe, key, { expirationTtl: TTL });
    await env.EVENTS.delete("stats"); // MRR may have changed
  }
  return new Response("ok"); // 200 for every verified event, including ones we ignore
}

// Payment-intent fields vary by payment method and API version; look in the likely places.
function details(o) {
  const pm = o.latest_payment_attempt?.payment_method ?? o.payment_method ?? {};
  const card = pm.card ?? {};
  const billing = card.billing ?? pm.billing ?? o.customer?.address ?? o.order?.shipping ?? {};
  const country = billing.address?.country_code ?? billing.country_code ?? card.issuer_country_code
    ?? o.customer?.address?.country_code ?? o.order?.shipping?.address?.country_code ?? null;
  const name = [billing.first_name ?? o.customer?.first_name, billing.last_name ?? o.customer?.last_name].filter(Boolean).join(" ")
    || card.name || null;
  const brand = card.brand ?? pm.type ?? null;
  return {
    country,
    customer: name,
    email: billing.email ?? o.customer?.email ?? null,
    method: brand ? [String(brand).toUpperCase(), card.last4 ? `•••• ${card.last4}` : null].filter(Boolean).join(" ") : null,
    cardCountry: card.issuer_country_code ?? null,
    funding: card.card_type ?? card.funding ?? null,
    description: o.descriptor ?? o.order?.products?.[0]?.name ?? null,
    orderId: o.merchant_order_id ?? null,
    risk: o.latest_payment_attempt?.risk_score ?? null,
  };
}

function summarize(evt, env) {
  const o = evt.data?.object ?? {};
  const name = evt.name ?? "";
  const at = Date.parse(evt.created_at ?? "") || Date.now();
  const who = o.customer?.email ?? o.descriptor ?? o.merchant_order_id ?? "";
  if (name === "payment_intent.created" || name === "payment_intent.pending") {
    return { kind: "intent", ref: o.id, label: who || "Checkout started", amount: o.amount ?? null, ...details(o),
             currency: o.currency ?? null, url: env.PAYMENT_URL.replace("{id}", o.id ?? ""), at };
  }
  if (name === "payment_intent.succeeded") {
    return { kind: "payment", ref: o.id, label: who || "Payment received", amount: o.captured_amount ?? o.amount ?? null, ...details(o),
             currency: o.currency ?? null, url: env.PAYMENT_URL.replace("{id}", o.id ?? ""), at };
  }
  if (name === "subscription.created" || name === "subscription.active") {
    return { kind: "subscriber", ref: o.id, label: o.customer?.email ?? o.customer_id ?? "New subscriber", amount: null,
             currency: o.currency ?? null, url: env.SUBSCRIPTION_URL.replace("{id}", o.id ?? ""), at };
  }
  return null;
}

async function events(url, env) {
  // ?latest=1 returns only a cursor (first run); ?after=<key> returns everything newer.
  const after = url.searchParams.get("after");
  const keys = [];
  let cursor;
  do {
    const page = await env.EVENTS.list({ prefix: "evt:", cursor });
    for (const k of page.keys) if (!after || k.name > after) keys.push(k.name);
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);
  keys.sort();
  const newest = keys.at(-1) ?? after ?? "evt:0";
  if (url.searchParams.has("latest")) return Response.json({ events: [], cursor: newest });
  const values = await Promise.all(keys.map(k => env.EVENTS.get(k, "json")));
  return Response.json({ events: values.filter(Boolean), cursor: newest });
}

async function airwallexToken(env) {
  const res = await fetch(`${env.AIRWALLEX_API}/api/v1/authentication/login`, {
    method: "POST",
    headers: { "x-client-id": env.AIRWALLEX_CLIENT_ID, "x-api-key": env.AIRWALLEX_API_KEY },
  });
  if (!res.ok) throw new Error(`airwallex login ${res.status}`);
  return (await res.json()).token;
}

async function get(env, token, path) {
  const res = await fetch(`${env.AIRWALLEX_API}${path}`, { headers: { authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`airwallex ${path} ${res.status}`);
  return res.json();
}

// Fraction of one billing cycle that falls in a month (every `period` units of DAY/WEEK/MONTH/YEAR).
function perMonth(recurring) {
  const n = Number(recurring?.period) || 1;
  const unit = String(recurring?.period_unit ?? "MONTH").toUpperCase();
  const months = { DAY: 12 / 365, WEEK: 12 / 52, MONTH: 1, YEAR: 12 }[unit] ?? 1;
  return 1 / (n * months);
}

// The item response shape is not fully documented; accept the price inline or nested.
function itemAmount(item) {
  const p = item.price ?? item;
  const qty = Number(item.quantity ?? 1);
  return Number(p.unit_amount ?? 0) * qty + Number(p.flat_amount ?? 0);
}

async function stats(env, fresh) {
  if (!fresh) {
    const cached = await env.EVENTS.get("stats", "json");
    if (cached) return Response.json(cached);
  }
  const token = await airwallexToken(env);
  const subs = [];
  let page;
  do {
    const q = new URLSearchParams({ status: "ACTIVE", page_size: "100" });
    if (page) q.set("page", page);
    const res = await get(env, token, `/api/v1/billing/subscriptions?${q}`);
    subs.push(...(res.items ?? []));
    page = res.page_after;
  } while (page);

  const byCurrency = {};
  for (const s of subs) {
    const items = await get(env, token, `/api/v1/billing/subscriptions/${s.id}/items`);
    const monthly = (items.items ?? []).reduce((sum, i) => sum + itemAmount(i), 0) * perMonth(s.recurring);
    byCurrency[s.currency] = (byCurrency[s.currency] ?? 0) + monthly;
  }
  // The deck shows the largest currency; the full breakdown is in byCurrency.
  const [currency, mrr] = Object.entries(byCurrency).sort((a, b) => b[1] - a[1])[0] ?? ["USD", 0];

  // Takings from the payments this relay has seen (kept 30 days), in the same currency.
  const now = Date.now(), dayAgo = now - 864e5;
  let today = 0, todayCount = 0, month = 0, monthCount = 0;
  let cursor;
  do {
    const page = await env.EVENTS.list({ prefix: "evt:", cursor });
    const vals = await Promise.all(page.keys.map(k => env.EVENTS.get(k.name, "json")));
    for (const e of vals) {
      if (e?.kind !== "payment" || String(e.ref).startsWith("test_") || e.currency !== currency) continue;
      month += Number(e.amount) || 0; monthCount++;
      if (e.at >= dayAgo) { today += Number(e.amount) || 0; todayCount++; }
    }
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);

  const round = n => Math.round(n * 100) / 100;
  const result = { subscribers: subs.length, mrr: round(mrr), currency, byCurrency,
                   today: round(today), todayCount, month: round(month), monthCount, at: now };
  await env.EVENTS.put("stats", JSON.stringify(result), { expirationTtl: STATS_CACHE_S });
  return Response.json(result);
}
