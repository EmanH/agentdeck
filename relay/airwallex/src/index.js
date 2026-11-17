// Receives Airwallex webhooks, keeps a short queue of "new payment / new subscriber" summaries in KV,
// and serves them plus MRR / active-subscriber stats to AgentDeck on the PC. The PC polls; nothing reaches it inbound.

// KV budget (free tier: 100k reads, 1k writes and 1k lists a day): every event lives in ONE record, "recent",
// so a poll is a single read and a webhook is a read plus a write. No KV list() calls anywhere.
const KEEP_MS = 30 * 24 * 3600 * 1000;
const KEEP_MAX = 500;
const TOLERANCE_MS = 5 * 60 * 1000;
const STATS_CACHE_S = 300;
const STATS_CACHE_KEY = "https://relay.internal/stats";

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/webhook") return await webhook(req, env);
      if (req.method === "POST" && url.pathname === "/test") {
        if (!authorized(req, env)) return new Response("unauthorized", { status: 401 });
        const id = `test_${Date.now()}`;
        const fake = { id, kind: "payment", ref: id, label: "test@example.com", amount: 49, currency: "AUD", at: Date.now(),
          url: env.PAYMENT_URL.replace("{id}", id), country: "NZ", customer: "Test Customer", email: "test@example.com",
          method: "VISA •••• 4242", cardCountry: "NZ", funding: "credit", description: "Pro plan (test)", orderId: "order-test", risk: 12 };
        return Response.json(await append(env, fake));
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
  if (summary) await append(env, { id: evt.id, ...summary });
  return new Response("ok"); // 200 for every verified event, including ones we ignore
}

async function loadRecent(env) {
  return (await env.EVENTS.get("recent", "json")) ?? [];
}

// Adds an event to "recent" unless it's a retry (same event id) or a second created/pending for the same checkout.
// Read-modify-write of one key: two webhooks in the same instant could drop one, which is fine for a desk notifier.
async function append(env, e) {
  const recent = await loadRecent(env);
  const dup = recent.some(r => r.id === e.id || (e.kind === "intent" && r.kind === "intent" && r.ref === e.ref));
  if (dup) return e;
  const now = Date.now();
  // Keys sort by arrival; the PC keeps the last one it saw as its cursor.
  e.key = `evt:${String(now).padStart(15, "0")}:${e.id}`;
  e.at ??= now;
  const arrived = r => Number(r.key.slice(4, 19));
  // Keep 30 days; test events only an hour.
  const kept = recent.filter(r => now - arrived(r) < (String(r.ref).startsWith("test_") ? 3600e3 : KEEP_MS));
  kept.push(e);
  await env.EVENTS.put("recent", JSON.stringify(kept.slice(-KEEP_MAX)));
  return e;
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
  const recent = await loadRecent(env);
  const after = url.searchParams.get("after");
  const newer = recent.filter(e => !after || e.key > after);
  const cursor = recent.at(-1)?.key ?? after ?? "evt:0";
  return Response.json({ events: url.searchParams.has("latest") ? [] : newer, cursor });
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
    const cached = await caches.default.match(STATS_CACHE_KEY);
    if (cached) return cached;
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
  for (const e of await loadRecent(env)) {
    if (e?.kind !== "payment" || String(e.ref).startsWith("test_") || e.currency !== currency) continue;
    month += Number(e.amount) || 0; monthCount++;
    if (e.at >= dayAgo) { today += Number(e.amount) || 0; todayCount++; }
  }

  const round = n => Math.round(n * 100) / 100;
  const result = { subscribers: subs.length, mrr: round(mrr), currency, byCurrency,
                   today: round(today), todayCount, month: round(month), monthCount, at: now };
  // Cached in the edge cache, not KV: free, and keeps KV writes for events.
  await caches.default.put(STATS_CACHE_KEY, Response.json(result, { headers: { "cache-control": `max-age=${STATS_CACHE_S}` } }));
  return Response.json(result);
}
