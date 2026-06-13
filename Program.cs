using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

// ───────────────────────────────────────────────────────────────────────────
//  MedTranslate — backend mínimo para hackathon (DeepSeek API)
//  POST /api/translate   -> traduce un estudio médico a lenguaje simple (JSON)
//  POST /api/seed-demo    -> pre-cachea ejemplos para la demo en vivo
// ───────────────────────────────────────────────────────────────────────────

const string SystemPrompt = """
Eres un comunicador en salud cuyo único trabajo es TRADUCIR información médica a
lenguaje simple, claro y empático para un paciente sin formación médica. NO eres
médico y NO diagnosticas.
REGLAS DE SEGURIDAD (inviolables):
- NUNCA des un diagnóstico definitivo. Usa lenguaje de posibilidad: "podría
  relacionarse con", "suele asociarse a".
- NUNCA recomiendes iniciar, suspender o cambiar medicamentos o dosis.
- Explica ÚNICAMENTE lo que aparezca en el texto. No inventes valores ni asumas datos.
- Si un valor sugiere posible emergencia, ponlo en senales_de_alarma y sube
  nivel_urgencia. Ante la duda, recomienda consultar a un profesional.
- Cierra recordando que esto no sustituye la valoración de su médico.
ESTILO: español claro nivel sexto grado, frases cortas, sin tecnicismos (si usas
uno, explícalo en paréntesis), tono cálido sin alarmar de más.
SALIDA: devuelve EXCLUSIVAMENTE un objeto JSON válido con este esquema, sin texto
antes ni después, sin markdown:
{ "resumen_simple": str, "hallazgos": [ {"parametro": str, "valor": str, "estado":
"alto|bajo|normal", "que_significa": str, "importancia": "alta|media|baja"} ],
"que_preguntar_al_medico": [str], "senales_de_alarma": [str], "nivel_urgencia":
"rutina|pronto|urgente", "disclaimer": str }
Si el texto no es información médica o es ilegible, devuelve el JSON con
resumen_simple explicando amablemente que no pudiste interpretarlo y pide pegar el
estudio completo.
""";

const string StandardDisclaimer =
    "Esta explicación es solo informativa y no sustituye la valoración de tu médico. " +
    "Consulta siempre con un profesional de la salud sobre tus resultados.";

var builder = WebApplication.CreateBuilder(args);

// CORS: cualquier origen (la app Flutter consume en local).
const string CorsPolicy = "AllowAll";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// HttpClient con timeout de 30s para llamar a DeepSeek.
builder.Services.AddHttpClient("deepseek", c => c.Timeout = TimeSpan.FromSeconds(30));

var app = builder.Build();
app.UseCors(CorsPolicy);

// ── Conexión a Redis (tolerante a fallos) ────────────────────────────────────
var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
IConnectionMultiplexer? redis = null;
try
{
    var opts = ConfigurationOptions.Parse(redisConn);
    opts.AbortOnConnectFail = false;          // no revienta si Redis no está arriba
    opts.ConnectTimeout = 2000;
    redis = ConnectionMultiplexer.Connect(opts);
    Console.WriteLine($"[INFO] Redis conectado en {redisConn}.");
}
catch (Exception ex)
{
    Console.WriteLine($"[WARN] Redis no disponible ({redisConn}): {ex.Message}. La caché queda deshabilitada.");
}

var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
    Console.WriteLine("[WARN] DEEPSEEK_API_KEY no está configurada. /api/translate devolverá la respuesta de fallback.");

// ── Ejemplos para sembrar la demo ────────────────────────────────────────────
var demoSamples = new List<TranslateRequest>
{
    // Anemia por déficit de hierro: hemoglobina, VCM y ferritina bajos.
    new(
        "Biometría hemática:\n" +
        "Hemoglobina: 9.1 g/dL (referencia 12.0-16.0)\n" +
        "VCM (volumen corpuscular medio): 72 fL (referencia 80-100)\n" +
        "HCM: 24 pg (referencia 27-33)\n" +
        "Ferritina: 8 ng/mL (referencia 30-300)\n" +
        "Hematocrito: 29% (referencia 36-46)",
        "laboratorio"),
    // Perfil metabólico mixto con valores levemente alterados.
    new(
        "Química sanguínea:\n" +
        "Glucosa en ayunas: 105 mg/dL (referencia 70-99)\n" +
        "Colesterol total: 215 mg/dL (referencia <200)\n" +
        "Colesterol HDL: 42 mg/dL (referencia >40)\n" +
        "Colesterol LDL: 140 mg/dL (referencia <100)\n" +
        "Triglicéridos: 180 mg/dL (referencia <150)",
        "laboratorio")
};

// ── Endpoints ────────────────────────────────────────────────────────────────
app.MapGet("/", () => "MedTranslate OK — usa POST /api/translate");

app.MapPost("/api/translate", async (TranslateRequest? req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Texto))
    {
        var empty = Fallback();
        empty.ResumenSimple = "No recibimos ningún texto para interpretar. Pega el estudio completo e intenta de nuevo.";
        return Results.Json(empty);
    }

    var res = await ProcessAsync(req);
    return Results.Json(res);
});

app.MapPost("/api/seed-demo", async (List<TranslateRequest>? items) =>
{
    // Si no mandan lista (o viene vacía), sembramos los ejemplos integrados.
    var toSeed = (items is { Count: > 0 }) ? items : demoSamples;
    var seeded = 0;
    foreach (var item in toSeed)
    {
        if (string.IsNullOrWhiteSpace(item.Texto)) continue;
        await ProcessAsync(item);
        seeded++;
    }
    return Results.Json(new { seeded, total = toSeed.Count, source = (items is { Count: > 0 }) ? "request" : "built-in" });
});

app.Run();

// ── Pipeline ─────────────────────────────────────────────────────────────────
async Task<TranslateResponse> ProcessAsync(TranslateRequest req)
{
    var key = "translate:" + Sha256(req.TipoDoc + "|" + req.Texto);

    // 1) Cache hit -> directo.
    var cached = await CacheGetAsync(key);
    if (cached is not null)
    {
        try
        {
            var hit = JsonSerializer.Deserialize<TranslateResponse>(cached);
            if (hit is not null) return hit;
        }
        catch { /* cache corrupta: la ignoramos y seguimos */ }
    }

    // 2) Llamar a DeepSeek.
    var (response, success) = await CallDeepSeekAsync(req);

    // 3) Cachear solo respuestas válidas (no el fallback de error).
    if (success)
        await CacheSetAsync(key, JsonSerializer.Serialize(response));

    return response;
}

async Task<(TranslateResponse Response, bool Success)> CallDeepSeekAsync(TranslateRequest req)
{
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("[ERROR] DEEPSEEK_API_KEY ausente; no se puede llamar al modelo.");
        return (Fallback(), false);
    }

    try
    {
        var userContent = $"Tipo de documento: {req.TipoDoc}. Texto del paciente: \"\"\"{req.Texto}\"\"\"";

        var payload = new
        {
            model = "deepseek-v4-flash",          // o "deepseek-v4-pro" / "deepseek-chat"
            max_tokens = 1500,
            temperature = 0.2,
            response_format = new { type = "json_object" }, // fuerza un objeto JSON válido
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = userContent }
            }
        };

        var client = httpFactory.CreateClient("deepseek");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(request);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[ERROR] DeepSeek respondió HTTP {(int)resp.StatusCode}.");
            return (Fallback(), false);
        }

        var body = await resp.Content.ReadAsStringAsync();
        var env = JsonSerializer.Deserialize<DeepSeekResponse>(body);
        var text = env?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("[ERROR] Respuesta de DeepSeek sin contenido.");
            return (Fallback(), false);
        }

        // Con JSON mode el contenido ya es un objeto JSON completo: se parsea directo.
        var parsed = JsonSerializer.Deserialize<TranslateResponse>(text.Trim());
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.ResumenSimple))
        {
            Console.WriteLine("[ERROR] El JSON del modelo no pudo parsearse al contrato.");
            return (Fallback(), false);
        }

        // Normaliza nulos para que el front siempre reciba el contrato completo.
        parsed.Hallazgos ??= new();
        parsed.QuePreguntarAlMedico ??= new();
        parsed.SenalesDeAlarma ??= new();
        if (string.IsNullOrWhiteSpace(parsed.NivelUrgencia)) parsed.NivelUrgencia = "rutina";
        if (string.IsNullOrWhiteSpace(parsed.Disclaimer)) parsed.Disclaimer = StandardDisclaimer;

        return (parsed, true);
    }
    catch (Exception ex)
    {
        // Logueamos el error real, NUNCA el texto médico crudo.
        Console.WriteLine($"[ERROR] Falló la llamada a DeepSeek: {ex.GetType().Name} - {ex.Message}");
        return (Fallback(), false);
    }
}

// ── Helpers de caché (degradación elegante) ──────────────────────────────────
async Task<string?> CacheGetAsync(string key)
{
    if (redis is null) return null;
    try
    {
        var db = redis.GetDatabase();
        var val = await db.StringGetAsync(key);
        return val.HasValue ? val.ToString() : null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Redis GET falló: {ex.Message}");
        return null;
    }
}

async Task CacheSetAsync(string key, string value)
{
    if (redis is null) return;
    try
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(key, value, TimeSpan.FromHours(24)); // TTL 24h
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Redis SET falló: {ex.Message}");
    }
}

// ── Utilidades ───────────────────────────────────────────────────────────────
static string Sha256(string input)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

static TranslateResponse Fallback() => new()
{
    ResumenSimple = "Tuvimos un problema procesando tu estudio en este momento. " +
                    "Por favor intenta de nuevo en unos segundos.",
    Hallazgos = new(),
    QuePreguntarAlMedico = new(),
    SenalesDeAlarma = new(),
    NivelUrgencia = "rutina",
    Disclaimer = StandardDisclaimer
};

// ── DTOs / Contrato ──────────────────────────────────────────────────────────
record TranslateRequest(
    [property: JsonPropertyName("texto")] string Texto,
    [property: JsonPropertyName("tipoDoc")] string TipoDoc);

class Hallazgo
{
    [JsonPropertyName("parametro")] public string Parametro { get; set; } = "";
    [JsonPropertyName("valor")] public string Valor { get; set; } = "";
    [JsonPropertyName("estado")] public string Estado { get; set; } = "normal"; // alto|bajo|normal
    [JsonPropertyName("que_significa")] public string QueSignifica { get; set; } = "";
    [JsonPropertyName("importancia")] public string Importancia { get; set; } = "media"; // alta|media|baja
}

class TranslateResponse
{
    [JsonPropertyName("resumen_simple")] public string ResumenSimple { get; set; } = "";
    [JsonPropertyName("hallazgos")] public List<Hallazgo> Hallazgos { get; set; } = new();
    [JsonPropertyName("que_preguntar_al_medico")] public List<string> QuePreguntarAlMedico { get; set; } = new();
    [JsonPropertyName("senales_de_alarma")] public List<string> SenalesDeAlarma { get; set; } = new();
    [JsonPropertyName("nivel_urgencia")] public string NivelUrgencia { get; set; } = "rutina"; // rutina|pronto|urgente
    [JsonPropertyName("disclaimer")] public string Disclaimer { get; set; } = "";
}

// Envoltura de la respuesta de DeepSeek (formato compatible con OpenAI).
class DeepSeekResponse
{
    [JsonPropertyName("choices")] public List<DeepSeekChoice>? Choices { get; set; }
}

class DeepSeekChoice
{
    [JsonPropertyName("message")] public DeepSeekMessage? Message { get; set; }
}

class DeepSeekMessage
{
    [JsonPropertyName("content")] public string? Content { get; set; }
}
