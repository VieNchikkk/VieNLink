// Приёмник подборок VieNLink для Cloudflare Workers.
//
// Что делает:
//   POST /presets      — принимает одну подтверждённую подборку от приложения (анонимно),
//                        агрегирует в KV и отдаёт 204. Только «cell:<MCCMNC>» и известные
//                        стратегии, лимит по IP, предел размера.
//   GET  /presets.json — отдаёт агрегат в формате provider-presets.json: этот адрес можно
//                        вписать в базу как uploadUrl (приёмник) и как источник базы.
//                        Поле uploadUrl в ответе — собственный адрес приёмника.
//   GET  /health       — «ok».
//
// Установка (5 минут, без моего участия):
//   1. Cloudflare → Workers & Pages → Create → Worker, вставить этот файл, Deploy.
//   2. Workers → KV → Create namespace «vienlink_presets».
//   3. В настройках воркера: Settings → Variables → KV Namespace Bindings:
//      имя PRESETS → namespace vienlink_presets. Deploy.
//   4. Взять URL вида https://<name>.<account>.workers.dev и прислать мне — впишу в
//      VieNLink.Core/Assets/provider-presets.json в поле uploadUrl.
//
// Данные: только оператор (MCCMNC), стратегии, режим QUIC и версия приложения.
// Ни SSID, ни адресов, ни идентификаторов устройства.

const MAX_PRESETS = 2000;
const MAX_BODY_BYTES = 2048;

const STRATEGIES = new Set([
  "SplitSni", "Split1", "MultiSplit", "SplitSniStart", "SplitSniMid", "SplitRecord",
  "RecordSplit", "RecordSplitStart", "RecordSplitEnd", "RecordSplitHalf", "Oob",
  "SplitSniEnd", "SplitStagger", "SplitSniBand", "SplitSlow", "DisorderSni",
  "MultiDisorder", "FakeSni", "FakeSniMd5", "FakeSni4", "FakeSni2", "FakeSni1",
  "FakeSni3", "FakeSni5", "FakeSni6", "FakeSni7",
]);

function json(body, status = 200) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  });
}

function pickStrategy(value) {
  if (typeof value !== "string") return "";
  const name = value.trim();
  return STRATEGIES.has(name) ? name : "";
}

async function aggregate(env) {
  const stored = await env.PRESETS.get("base", "json");
  if (stored && Array.isArray(stored.presets)) return stored;
  return { version: 1, uploadUrl: "", presets: [] };
}

async function mergePreset(env, incoming) {
  const base = await aggregate(env);
  const existing = base.presets.find((item) => item.key === incoming.key);

  if (!existing) {
    base.presets.push(incoming);
  } else {
    // Держим связку с наибольшим числом подтверждений: один телефон не должен
    // перебивать статистику остальных.
    const better = incoming.successes > existing.successes;
    const merged = {
      ...existing,
      operator: incoming.operator || existing.operator,
      successes: Math.max(existing.successes, incoming.successes),
      lastSuccessUtc: incoming.lastSuccessUtc || existing.lastSuccessUtc,
      videoSeenUtc: incoming.videoSeenUtc || existing.videoSeenUtc,
      videoTransport: incoming.videoTransport || existing.videoTransport,
      sources: (existing.sources || 1) + 1,
    };
    if (better) {
      merged.page = incoming.page;
      merged.video = incoming.video;
      merged.blockQuic = incoming.blockQuic;
    }

    const index = base.presets.indexOf(existing);
    base.presets[index] = merged;
  }

  base.presets.sort((a, b) => (b.successes || 0) - (a.successes || 0));
  if (base.presets.length > MAX_PRESETS) base.presets.length = MAX_PRESETS;
  base.version = 1;
  base.updatedUtc = new Date().toISOString();
  base.uploadUrl = new URL(env.SELF_URL || "https://collector.invalid/presets").origin + "/presets";
  await env.PRESETS.put("base", JSON.stringify(base));
  return base;
}

async function rateLimited(env, ip) {
  if (!ip) return false;
  const key = "rl:" + ip;
  const current = Number(await env.PRESETS.get(key)) || 0;
  if (current >= 60) return true;
  await env.PRESETS.put(key, String(current + 1), { expirationTtl: 600 });
  return false;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const ip = request.headers.get("cf-connecting-ip");

    if (url.pathname === "/health") return new Response("ok");

    if (url.pathname === "/presets.json") {
      const base = await aggregate(env);
      return json({ version: 1, updatedUtc: base.updatedUtc, uploadUrl: base.uploadUrl, presets: base.presets });
    }

    if (url.pathname !== "/presets") return json({ error: "not found" }, 404);

    if (request.method !== "POST") return json({ error: "post only" }, 405);
    if (await rateLimited(env, ip)) return json({ error: "too many" }, 429);

    const size = Number(request.headers.get("content-length") || 0);
    if (size > MAX_BODY_BYTES) return json({ error: "too large" }, 413);

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "bad json" }, 400);
    }

    const key = typeof body.key === "string" ? body.key.trim() : "";
    if (!/^cell:[0-9]{5,6}$/.test(key)) return json({ error: "bad key" }, 400);

    const page = pickStrategy(body.page);
    if (!page) return json({ error: "bad page" }, 400);

    const video = pickStrategy(body.video);
    const transport = body.videoTransport === "quic" || body.videoTransport === "tcp"
      ? body.videoTransport
      : "";

    const preset = {
      key,
      operator: typeof body.operator === "string" ? body.operator.slice(0, 40) : "",
      page,
      video,
      blockQuic: body.blockQuic === true,
      videoTransport: transport,
      successes: Math.max(1, Math.min(100000, Number(body.successes) || 1)),
      lastSuccessUtc: new Date().toISOString(),
      videoSeenUtc: transport ? new Date().toISOString() : "",
      sources: 1,
    };

    await mergePreset(env, preset);
    return new Response(null, { status: 204 });
  },
};
