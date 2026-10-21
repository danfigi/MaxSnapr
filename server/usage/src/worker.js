import DASHBOARD_HTML from "./dashboard.html";

// MaxSnapr anonymous usage counter (Cloudflare Worker + D1).
//
// POST /v1/ping   from the app, at most once a day per copy. The body has no identifier: version, Windows
//                 build, UI language, install kind, "first of the week/month" flags and yesterday's feature
//                 counts. The country comes from Cloudflare's edge; the IP address is never read or stored.
// GET  /          private dashboard (HTML), and GET /api/stats its data. Both need the DASHBOARD_KEY secret,
//                 sent once as ?key=... and then kept in an HttpOnly cookie.

const FEATURES = new Set([
  "capture", "capture.region", "capture.window", "capture.fullscreen", "capture.scrolling", "capture.panoramic",
  "capture.repeat", "capture.steps", "ocr", "video", "video.mic", "video.system_audio", "video.webcam",
  "video.background", "video.gif", "editor.open", "editor.arrow", "editor.text", "editor.callout", "editor.shape",
  "editor.step", "editor.pen", "editor.blur", "editor.magnify", "editor.spotlight", "editor.stamp", "editor.crop",
  "smart_redact", "export", "share", "mobile_receive", "template", "library.open",
]);
const MAX_BODY = 4096;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    try {
      if (url.pathname === "/v1/ping" && request.method === "POST") return await ping(request, env);
      if (url.pathname === "/api/stats" && request.method === "GET") return await stats(request, env, url);
      if (url.pathname === "/" && request.method === "GET") return await dashboard(request, env, url);
      return new Response("Not found", { status: 404 });
    } catch (e) {
      return new Response("Error", { status: 500 });
    }
  },
};

// ------------------------------------------------------------------------------------------------ ingest

const clean = (v, re, max, fallback) => (typeof v === "string" && v.length <= max && re.test(v) ? v : fallback);

async function ping(request, env) {
  const len = Number(request.headers.get("content-length") || "0");
  if (len > MAX_BODY) return new Response(null, { status: 413 });
  const text = await request.text();
  if (text.length > MAX_BODY) return new Response(null, { status: 413 });
  let b;
  try { b = JSON.parse(text); } catch { return new Response(null, { status: 400 }); }
  if (!b || typeof b !== "object") return new Response(null, { status: 400 });

  const version = clean(b.v, /^\d{1,3}\.\d{1,3}\.\d{1,4}$/, 12, null);
  if (!version) return new Response(null, { status: 400 });
  const os = clean(b.os, /^(10|11) \d{4,6}$/, 12, "?");
  const lang = clean(b.lang, /^[a-z]{2,3}$/, 3, "?");
  const install = b.install === "portable" ? "portable" : "setup";
  const country = clean(request.cf && request.cf.country, /^[A-Z]{2}$/, 2, "XX");
  const day = new Date().toISOString().slice(0, 10);
  const nw = b.nw === true ? 1 : 0, nm = b.nm === true ? 1 : 0, first = b.first === true ? 1 : 0;

  const stmts = [env.DB.prepare(
    `INSERT INTO daily (day, version, os, country, lang, install, users, new_week, new_month, first_run)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, 1, ?7, ?8, ?9)
     ON CONFLICT (day, version, os, country, lang, install) DO UPDATE SET
       users = users + 1, new_week = new_week + ?7, new_month = new_month + ?8, first_run = first_run + ?9`
  ).bind(day, version, os, country, lang, install, nw, nm, first)];

  // Yesterday's feature counts, credited to the day they happened (the copy reports them the next time it starts).
  const fday = clean(b.fd, /^\d{4}-\d{2}-\d{2}$/, 10, null);
  if (fday && b.f && typeof b.f === "object" && daysBetween(fday, day) >= 0 && daysBetween(fday, day) <= 31) {
    let n = 0;
    for (const [name, count] of Object.entries(b.f)) {
      if (!FEATURES.has(name) || !Number.isInteger(count) || count < 1 || ++n > 64) continue;
      stmts.push(env.DB.prepare(
        `INSERT INTO features (day, version, feature, uses, users) VALUES (?1, ?2, ?3, ?4, 1)
         ON CONFLICT (day, version, feature) DO UPDATE SET uses = uses + ?4, users = users + 1`
      ).bind(fday, version, name, Math.min(count, 1000)));
    }
  }
  await env.DB.batch(stmts);
  return new Response(null, { status: 204 });
}

function daysBetween(a, b) {
  return Math.round((Date.parse(b + "T00:00:00Z") - Date.parse(a + "T00:00:00Z")) / 86400000);
}

// ------------------------------------------------------------------------------------------------ dashboard

async function authorised(request, env, url) {
  const want = env.DASHBOARD_KEY;
  if (!want) return { ok: false };
  const cookie = (request.headers.get("cookie") || "").split(/;\s*/).find((c) => c.startsWith("mxk="));
  const given = url.searchParams.get("key") || (cookie ? decodeURIComponent(cookie.slice(4)) : "");
  const ok = given.length === want.length && timingSafeEqual(given, want);
  return { ok, fromQuery: ok && url.searchParams.has("key") };
}

function timingSafeEqual(a, b) {
  let r = 0;
  for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return r === 0;
}

const SECURITY_HEADERS = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer",
  "x-frame-options": "DENY",
};

async function dashboard(request, env, url) {
  const auth = await authorised(request, env, url);
  if (!auth.ok) return new Response("Acceso restringido.", { status: 401, headers: { "content-type": "text/plain; charset=utf-8", ...SECURITY_HEADERS } });
  if (auth.fromQuery) {
    // Move the key from the address bar into a cookie so it does not stay in the history.
    return new Response(null, {
      status: 303,
      headers: { location: "/", "set-cookie": `mxk=${encodeURIComponent(url.searchParams.get("key"))}; Path=/; Max-Age=31536000; HttpOnly; Secure; SameSite=Strict`, ...SECURITY_HEADERS },
    });
  }
  return new Response(DASHBOARD_HTML, {
    headers: {
      "content-type": "text/html; charset=utf-8",
      "content-security-policy": "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:",
      ...SECURITY_HEADERS,
    },
  });
}

async function stats(request, env, url) {
  const auth = await authorised(request, env, url);
  if (!auth.ok) return new Response(null, { status: 401, headers: SECURITY_HEADERS });
  const days = Math.min(Math.max(parseInt(url.searchParams.get("days") || "90", 10) || 90, 7), 730);
  const since = new Date(Date.now() - (days - 1) * 86400000).toISOString().slice(0, 10);
  const q = (sql, ...args) => env.DB.prepare(sql).bind(...args).all().then((r) => r.results);
  const [daily, versions, os, countries, langs, installs, features] = await Promise.all([
    q(`SELECT day, SUM(users) users, SUM(new_week) new_week, SUM(new_month) new_month, SUM(first_run) first_run
       FROM daily WHERE day >= ?1 GROUP BY day ORDER BY day`, since),
    q(`SELECT day, version, SUM(users) users FROM daily WHERE day >= ?1 GROUP BY day, version ORDER BY day`, since),
    q(`SELECT os k, SUM(users) users FROM daily WHERE day >= ?1 GROUP BY os ORDER BY users DESC`, since),
    q(`SELECT country k, SUM(users) users FROM daily WHERE day >= ?1 GROUP BY country ORDER BY users DESC`, since),
    q(`SELECT lang k, SUM(users) users FROM daily WHERE day >= ?1 GROUP BY lang ORDER BY users DESC`, since),
    q(`SELECT install k, SUM(users) users FROM daily WHERE day >= ?1 GROUP BY install ORDER BY users DESC`, since),
    q(`SELECT feature k, SUM(uses) uses, SUM(users) users FROM features WHERE day >= ?1 GROUP BY feature ORDER BY users DESC`, since),
  ]);
  return Response.json({ since, days, daily, versions, os, countries, langs, installs, features }, { headers: SECURITY_HEADERS });
}

