using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppiieNet.Client.Models;

namespace AppiieNet.Client.Services;

public sealed class ApiException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }

    public ApiException(string code, string message, int httpStatus = 0)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }
}

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        // MySQL 的 DECIMAL 经 PHP 返回的是字符串（如 "59.70"），这里允许按字符串读取数字
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string _baseUrl;

    public ApiClient(string baseUrl)
    {
        _baseUrl = Normalize(baseUrl);
    }

    public void UpdateBaseUrl(string baseUrl) => _baseUrl = Normalize(baseUrl);

    private static string Normalize(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url.EndsWith("api.php", StringComparison.OrdinalIgnoreCase)
            ? url
            : url.TrimEnd('/') + "/api.php";
    }

    public async Task<T> PostAsync<T>(string route, object? body = null, string? token = null,
        CancellationToken ct = default)
    {
        var sep = _baseUrl.Contains('?') ? "&" : "?";
        var url = _baseUrl + sep + "r=" + Uri.EscapeDataString(route);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (body != null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body, Opts),
                Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        ApiEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope>(text, Opts);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope == null || !envelope.Ok)
        {
            string code = "HTTP_" + (int)resp.StatusCode;
            string message = resp.ReasonPhrase ?? "请求失败";
            if (envelope?.Data is { } data && data.TryGetProperty("error", out var e)
                && data.TryGetProperty("message", out var m))
            {
                code = e.GetString() ?? code;
                message = m.GetString() ?? message;
            }
            throw new ApiException(code, message, (int)resp.StatusCode);
        }

        if (envelope.Data is not { } payload)
        {
            throw new ApiException("EMPTY", "服务器返回为空", (int)resp.StatusCode);
        }

        return JsonSerializer.Deserialize<T>(payload.GetRawText(), Opts)
            ?? throw new ApiException("PARSE", "响应解析失败", (int)resp.StatusCode);
    }

    public Task<PingData> PingAsync(CancellationToken ct = default) =>
        PostAsync<PingData>("ping", null, null, ct);

    public Task<object> RegisterAsync(string username, string password, string email = "",
        string inviteCode = "", string emailCode = "", CancellationToken ct = default) =>
        PostAsync<object>("register",
            new { username, password, email, invite_code = inviteCode, email_code = emailCode }, null, ct);

    public Task<LoginData> LoginAsync(string username, string password, string deviceUid,
        string deviceName, string platform = "windows", CancellationToken ct = default) =>
        PostAsync<LoginData>("login",
            new { username, password, device_uid = deviceUid, device_name = deviceName, platform },
            null, ct);

    public Task<object> ResetPasswordAsync(string username, string email, string newPassword,
        string emailCode = "", CancellationToken ct = default) =>
        PostAsync<object>("reset_password",
            new { username, email, new_password = newPassword, email_code = emailCode }, null, ct);

    public Task<PublicConfig> PublicConfigAsync(CancellationToken ct = default) =>
        PostAsync<PublicConfig>("public_config", null, null, ct);

    public Task<object> SendEmailCodeAsync(string email, string purpose, CancellationToken ct = default) =>
        PostAsync<object>("send_email_code", new { email, purpose }, null, ct);

    public Task<PlanListData> PlansAsync(string token, CancellationToken ct = default) =>
        PostAsync<PlanListData>("plans", null, token, ct);

    public Task<OrderCreateData> OrderCreateAsync(string levelCode, int months, string token,
        CancellationToken ct = default) =>
        PostAsync<OrderCreateData>("order_create",
            new { level_code = levelCode, months }, token, ct);

    public Task<OrderListData> OrderListAsync(string token, CancellationToken ct = default) =>
        PostAsync<OrderListData>("order_list", null, token, ct);

    public async Task<OrderInfo> OrderQueryAsync(string orderNo, string token,
        CancellationToken ct = default)
    {
        var data = await PostAsync<OrderQueryData>("order_query", new { order_no = orderNo },
            token, ct);
        return data.Order;
    }

    public Task<object> OrderCancelAsync(string orderNo, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("order_cancel", new { order_no = orderNo }, token, ct);

    public Task<InviteBuyData> InviteBuyAsync(CancellationToken ct = default) =>
        PostAsync<InviteBuyData>("invite_buy", null, null, ct);

    public Task<InviteOrderData> InviteOrderQueryAsync(string orderNo, string guestToken,
        CancellationToken ct = default) =>
        PostAsync<InviteOrderData>("invite_order_query",
            new { order_no = orderNo, guest_token = guestToken }, null, ct);

    public Task<LoginData> MeAsync(string token, CancellationToken ct = default) =>
        PostAsync<LoginData>("me", null, token, ct);

    public Task<RoomConfig> CreateNetworkAsync(string name, string nodeId, string token,
        CancellationToken ct = default) =>
        PostAsync<RoomConfig>("network_create", new { name, node_id = nodeId }, token, ct);

    public Task<RoomConfig> JoinNetworkAsync(string code, string token,
        CancellationToken ct = default) =>
        PostAsync<RoomConfig>("network_join", new { code }, token, ct);

    public Task<RoomConfig> GetNetworkConfigAsync(int networkId, string token,
        CancellationToken ct = default) =>
        PostAsync<RoomConfig>("network_config", new { network_id = networkId }, token, ct);

    public Task<NetworkListData> ListNetworksAsync(string token, CancellationToken ct = default) =>
        PostAsync<NetworkListData>("network_list", null, token, ct);

    public Task<object> HeartbeatAsync(string token, int? networkId = null,
        CancellationToken ct = default) =>
        PostAsync<object>("heartbeat", new { network_id = networkId ?? 0 }, token, ct);

    public Task<NetworkInfoData> NetworkInfoAsync(int networkId, string token,
        CancellationToken ct = default) =>
        PostAsync<NetworkInfoData>("network_info", new { network_id = networkId }, token, ct);

    public Task<NodesData> NodesAsync(string token, CancellationToken ct = default) =>
        PostAsync<NodesData>("nodes", null, token, ct);

    public Task<object> KickMemberAsync(int networkId, int memberId, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("network_member_kick",
            new { network_id = networkId, member_id = memberId }, token, ct);

    public Task<object> RemarkMemberAsync(int networkId, int memberId, string remark, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("network_member_remark",
            new { network_id = networkId, member_id = memberId, remark }, token, ct);

    public Task<object> TransferNetworkAsync(int networkId, int userId, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("network_transfer",
            new { network_id = networkId, user_id = userId }, token, ct);

    public Task<LoginData> RenameDeviceAsync(int deviceId, string name, string token,
        CancellationToken ct = default) =>
        PostAsync<LoginData>("device_rename", new { device_id = deviceId, name }, token, ct);

    public Task<LoginData> RemoveDeviceAsync(int deviceId, string token,
        CancellationToken ct = default) =>
        PostAsync<LoginData>("device_remove", new { device_id = deviceId }, token, ct);

    public Task<object> LeaveNetworkAsync(int networkId, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("network_leave", new { network_id = networkId }, token, ct);

    /// <summary>申请删除组网的临时验证码（房主专用）</summary>
    public Task<DeleteCodeData> NetworkDeleteCodeAsync(int networkId, string token,
        CancellationToken ct = default) =>
        PostAsync<DeleteCodeData>("network_delete_code", new { network_id = networkId }, token, ct);

    /// <summary>带临时验证码删除组网（房主专用）</summary>
    public Task<object> NetworkDeleteAsync(int networkId, string code, string token,
        CancellationToken ct = default) =>
        PostAsync<object>("network_delete", new { network_id = networkId, code }, token, ct);
}
