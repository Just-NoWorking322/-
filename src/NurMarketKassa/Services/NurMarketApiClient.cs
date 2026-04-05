using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Auth;

namespace NurMarketKassa.Services;

/// <summary>
/// HTTP-клиент к Nur CRM (логика как в PRINTER_EXE_CHAIN/api_client.py JwtClient).
/// </summary>
public sealed class NurMarketApiClient : IDisposable
{
    public const string AuthInvalidHintRu =
        "Сессия недействительна (часто из‑за входа с другого ПК или телефона). " +
        "Нажмите «Выйти» в кассе и войдите снова.";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonWrite = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly JsonSerializerOptions _jsonRead = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public JsonElement UserPayload { get; private set; }
    public string? ActiveBranchId { get; private set; }

    public NurMarketApiClient(HttpClient http, AppSettings settings)
    {
        _http = http;
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/') + "/";
        _http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        UserPayload = default;
    }

    /// <summary>Проверка доступности API (аналог can_reach_api).</summary>
    public async Task<bool> CanReachApiAsync(CancellationToken ct = default)
    {
        foreach (var path in new[] { "", "api/users/auth/login/" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500)
                    return true;
            }
            catch
            {
                /* next */
            }
        }

        return false;
    }

    public async Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = new LoginRequest { Email = email.Trim(), Password = password };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/users/auth/login/", content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                JsonElement? payload = TryParse(text);
                throw new ApiException(ApiErrorParser.Parse(resp, text), (int)resp.StatusCode, payload);
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            ApplyLoginResponse(root);
            return root;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RefreshAccessAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(RefreshToken))
                return false;
            var body = new RefreshRequest { Refresh = RefreshToken };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/users/auth/refresh/", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
            {
                AccessToken = acc.GetString();
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ClearSession()
    {
        AccessToken = null;
        RefreshToken = null;
        UserPayload = default;
        ActiveBranchId = null;
    }

    /// <summary>GET /api/users/profile/</summary>
    public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "api/users/profile/", null, null, ct);

    /// <summary>
    /// Универсальный запрос с Bearer и query branch=… (как branch_params() в Python).
    /// </summary>
    /// <param name="requestTimeout">Ограничение времени запроса (например scan 22 с).</param>
    public async Task<JsonElement> RequestAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await SendOnceAsync(method, relativePath, jsonBody, query, retryRefresh: true, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> SendOnceAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        bool retryRefresh,
        CancellationToken ct)
    {
        var uri = BuildUri(relativePath, query);
        using var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, _jsonWrite);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && retryRefresh && !string.IsNullOrEmpty(RefreshToken))
        {
            if (await RefreshAccessUnlockedAsync(ct).ConfigureAwait(false))
                return await SendOnceAsync(method, relativePath, jsonBody, query, retryRefresh: false, ct).ConfigureAwait(false);
            ClearSession();
        }
        else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken))
        {
            ClearSession();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? AuthInvalidHintRu
                : ApiErrorParser.Parse(resp, text);
            JsonElement? payload = TryParse(text);
            throw new ApiException(msg, (int)resp.StatusCode, payload);
        }

        if (string.IsNullOrWhiteSpace(text))
            return default;

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<bool> RefreshAccessUnlockedAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(RefreshToken))
            return false;
        var body = new RefreshRequest { Refresh = RefreshToken };
        using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("api/users/auth/refresh/", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return false;
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
        {
            AccessToken = acc.GetString();
            return true;
        }

        return false;
    }

    private Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string>? query)
    {
        var path = relativePath.TrimStart('/');
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(ActiveBranchId))
            qs.Add("branch=" + Uri.EscapeDataString(ActiveBranchId));
        if (query is not null)
        {
            foreach (var kv in query)
                qs.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""));
        }

        var rel = qs.Count == 0 ? path : path + "?" + string.Join("&", qs);
        return new Uri(_http.BaseAddress!, rel);
    }

    private void ApplyLoginResponse(JsonElement root)
    {
        if (root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String)
            AccessToken = a.GetString();
        if (root.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.String)
            RefreshToken = r.GetString();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("access") || prop.NameEquals("refresh"))
                    continue;
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        if (bytes.Length > 2)
        {
            using var doc = JsonDocument.Parse(bytes);
            UserPayload = doc.RootElement.Clone();
        }
        else
            UserPayload = default;

        SyncBranchFromUser();
    }

    /// <summary>
    /// После GET /api/users/profile/ — в JWT часто нет филиала; запросы с ?branch= должны использовать id из профиля.
    /// </summary>
    public void ApplyBranchFromProfile(JsonElement profile)
    {
        var bid = TryExtractBranchId(profile);
        if (!string.IsNullOrEmpty(bid))
            ActiveBranchId = bid;
    }

    private void SyncBranchFromUser() => ActiveBranchId = TryExtractBranchId(UserPayload);

    private static string? TryExtractBranchId(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        if (user.TryGetProperty("primary_branch_id", out var pb) && pb.ValueKind == JsonValueKind.String)
        {
            var s = pb.GetString();
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (user.TryGetProperty("branch_ids", out var bids) && bids.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in bids.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }
        }

        return null;
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GET /api/construction/cashboxes/ — при 404 возвращает [], как в Python.</summary>
    public async Task<JsonElement> ConstructionCashboxesListAsync(CancellationToken ct = default)
    {
        try
        {
            return await RequestAsync(HttpMethod.Get, "api/construction/cashboxes/", null, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            using var d = JsonDocument.Parse("[]");
            return d.RootElement.Clone();
        }
    }

    /// <summary>GET /api/construction/shifts/</summary>
    public Task<JsonElement> ConstructionShiftsListAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "api/construction/shifts/", null, null, ct);

    /// <summary>POST открытия смены — два URL и два варианта тела (как construction_shift_open).</summary>
    public async Task<JsonElement> ConstructionShiftOpenAsync(
        string cashboxId,
        string openingCash = "0.00",
        CancellationToken ct = default)
    {
        var paths = new[] { "api/construction/shifts/open/", "api/construction/shift/open/" };
        var payloads = new[]
        {
            new Dictionary<string, string> { ["cashbox"] = cashboxId.Trim(), ["opening_cash"] = openingCash.Trim() },
            new Dictionary<string, string> { ["cashbox_id"] = cashboxId.Trim(), ["opening_cash"] = openingCash.Trim() },
        };

        ApiException? last = null;
        foreach (var path in paths)
        {
            for (var i = 0; i < payloads.Length; i++)
            {
                try
                {
                    return await RequestAsync(HttpMethod.Post, path, payloads[i], null, ct).ConfigureAwait(false);
                }
                catch (ApiException e)
                {
                    last = e;
                    if (e.StatusCode == 404)
                        break;
                    if (e.StatusCode == 400 && i + 1 < payloads.Length)
                        continue;
                    throw;
                }
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Не удалось открыть смену", 404);
    }

    /// <summary>POST закрытия смены — два URL (как construction_shift_close).</summary>
    public async Task<JsonElement> ConstructionShiftCloseAsync(
        string shiftId,
        string? closingCash = null,
        CancellationToken ct = default)
    {
        var sid = Uri.EscapeDataString(shiftId.Trim());
        var paths = new[]
        {
            $"api/construction/shifts/{sid}/close/",
            $"api/construction/shift/{sid}/close/",
        };

        var body = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(closingCash))
            body["closing_cash"] = closingCash.Trim();

        ApiException? last = null;
        foreach (var path in paths)
        {
            try
            {
                return await RequestAsync(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);
            }
            catch (ApiException e)
            {
                last = e;
                if (e.StatusCode == 404)
                    continue;
                throw;
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Не удалось закрыть смену", 404);
    }

    /// <summary>POST /api/main/pos/sales/start/</summary>
    public Task<JsonElement> PosSalesStartAsync(string? cashboxId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(cashboxId))
            body["cashbox_id"] = cashboxId.Trim();
        return RequestAsync(HttpMethod.Post, "api/main/pos/sales/start/", body, null, ct);
    }

    /// <summary>GET /api/main/pos/carts/{id}/</summary>
    public Task<JsonElement> PosCartGetAsync(string cartId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        return RequestAsync(HttpMethod.Get, $"api/main/pos/carts/{id}/", null, null, ct);
    }

    /// <summary>POST /api/main/pos/sales/{id}/scan/ — таймаут как в Python (3+22 с).</summary>
    public Task<JsonElement> PosScanAsync(string cartId, string barcode, string? quantity = null, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["barcode"] = barcode.Trim() };
        if (!string.IsNullOrEmpty(quantity))
            body["quantity"] = quantity;
        return RequestAsync(HttpMethod.Post, $"api/main/pos/sales/{id}/scan/", body, null, ct, TimeSpan.FromSeconds(22));
    }

    /// <summary>PATCH /api/main/pos/carts/{cart}/items/{item}/</summary>
    public Task<JsonElement> PosCartItemPatchAsync(
        string cartId,
        string itemId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/items/{i}/", body, null, ct);
    }

    /// <summary>DELETE /api/main/pos/carts/{cart}/items/{item}/</summary>
    public Task<JsonElement> PosCartItemDeleteAsync(string cartId, string itemId, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return RequestAsync(HttpMethod.Delete, $"api/main/pos/carts/{c}/items/{i}/", null, null, ct);
    }

    /// <summary>
    /// POST checkout — два URL, таймаут до 90 с; при 400 без cash_received для безнала — повтор с 0.00 (как pos_checkout).
    /// </summary>
    public async Task<JsonElement> PosCheckoutAsync(
        string cartId,
        Dictionary<string, string> body,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var paths = new[]
        {
            $"api/main/pos/sales/{id}/checkout/",
            $"api/main/pos/carts/{id}/checkout/",
        };
        var timeout = TimeSpan.FromSeconds(90);
        ApiException? last404 = null;

        foreach (var path in paths)
        {
            try
            {
                return await RequestAsync(HttpMethod.Post, path, body, null, ct, timeout).ConfigureAwait(false);
            }
            catch (ApiException e)
            {
                if (e.StatusCode == 404)
                {
                    last404 = e;
                    continue;
                }

                var pm = body.GetValueOrDefault("payment_method") ?? "";
                if (e.StatusCode == 400
                    && !body.ContainsKey("cash_received")
                    && !string.Equals(pm, "cash", StringComparison.OrdinalIgnoreCase))
                {
                    var retry = new Dictionary<string, string>(body) { ["cash_received"] = "0.00" };
                    try
                    {
                        return await RequestAsync(HttpMethod.Post, path, retry, null, ct, timeout).ConfigureAwait(false);
                    }
                    catch (ApiException)
                    {
                        throw e;
                    }
                }

                throw;
            }
        }

        if (last404 != null)
            throw last404;
        throw new ApiException("Checkout: пустой список путей", 500);
    }

    /// <summary>GET /api/main/pos/sales/{id}/receipt/ — текст чека для печати.</summary>
    public Task<JsonElement> PosSaleReceiptAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return RequestAsync(HttpMethod.Get, $"api/main/pos/sales/{id}/receipt/", null, null, ct);
    }

    /// <summary>PATCH /api/main/pos/carts/{id}/ — скидка на чек и др.</summary>
    public Task<JsonElement> PosCartPatchAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        return RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/", body, null, ct);
    }

    /// <summary>POST /api/main/pos/sales/{id}/add-item/ — как pos_add_item (таймаут до 28 с).</summary>
    public Task<JsonElement> PosAddItemAsync(
        string cartId,
        string productId,
        string? quantity = null,
        string? unitPrice = null,
        string? discountTotal = null,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["product_id"] = productId.Trim() };
        if (!string.IsNullOrWhiteSpace(quantity))
            body["quantity"] = quantity.Trim();
        if (!string.IsNullOrWhiteSpace(unitPrice))
            body["unit_price"] = unitPrice.Trim();
        if (!string.IsNullOrWhiteSpace(discountTotal))
            body["discount_total"] = discountTotal.Trim();
        return RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{id}/add-item/",
            body,
            null,
            ct,
            TimeSpan.FromSeconds(28));
    }

    /// <summary>Поиск товаров по названию (как products_search).</summary>
    public async Task<List<JsonElement>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return new List<JsonElement>();

        var candidates = new (string Path, Dictionary<string, string> Qs)[]
        {
            ("api/main/products/list/", new Dictionary<string, string> { ["search"] = q, ["page"] = "1" }),
            ("api/main/products/", new Dictionary<string, string> { ["search"] = q }),
        };

        ApiException? last = null;
        foreach (var (path, qs) in candidates)
        {
            try
            {
                var data = await RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                var items = UnwrapList(data);
                if (items.Count > limit)
                    items.RemoveRange(limit, items.Count - limit);
                return items;
            }
            catch (ApiException e)
            {
                last = e;
                if (e.StatusCode == 404)
                    continue;
                throw;
            }
        }

        if (last != null)
            throw last;
        return new List<JsonElement>();
    }

    /// <summary>Страницы каталога без поиска (как products_catalog).</summary>
    public async Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default)
    {
        var outList = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new[] { "api/main/products/list/", "api/main/products/" };
        ApiException? last404 = null;

        foreach (var path in paths)
        {
            outList.Clear();
            seen.Clear();
            try
            {
                for (var page = 1; page <= Math.Max(1, maxPages); page++)
                {
                    var qs = new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture) };
                    var data = await RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                    var items = UnwrapList(data);
                    if (items.Count == 0)
                        break;
                    foreach (var p in items)
                    {
                        var pid = TryProductIdString(p);
                        if (string.IsNullOrEmpty(pid) || seen.Contains(pid))
                            continue;
                        seen.Add(pid);
                        outList.Add(p);
                        if (outList.Count >= limit)
                            return outList;
                    }
                }

                if (outList.Count > 0)
                    return outList;
            }
            catch (ApiException e)
            {
                last404 = e;
                if (e.StatusCode != 404)
                    throw;
            }
        }

        if (last404 != null && outList.Count == 0)
            throw last404;
        return outList;
    }

    /// <summary>Карточка товара с картинками (как products_detail).</summary>
    public async Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default)
    {
        var pid = Uri.EscapeDataString(productId.Trim());
        if (pid.Length == 0)
            return null;
        foreach (var path in new[] { $"api/main/products/{pid}/", $"api/main/products/list/{pid}/" })
        {
            try
            {
                var data = await RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                if (data.ValueKind != JsonValueKind.Object)
                    continue;
                if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object &&
                    inner.TryGetProperty("id", out _))
                    return inner.Clone();
                if (data.TryGetProperty("id", out _))
                    return data.Clone();
            }
            catch (ApiException e)
            {
                if (e.StatusCode is 404 or 405 or 410)
                    continue;
                return null;
            }
        }

        return null;
    }

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    public async Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return null;
        if (string.IsNullOrEmpty(AccessToken))
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<JsonElement> UnwrapList(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                list.Add(el.Clone());
            return list;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                list.Add(el.Clone());
        }

        return list;
    }

    private static string? TryProductIdString(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id))
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    public void Dispose() => _gate.Dispose();
}
