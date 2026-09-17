using System.Text.Json.Serialization;

namespace AppiieNet.Client.Models;

public sealed class ApiEnvelope
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("data")] public System.Text.Json.JsonElement? Data { get; set; }
}

public sealed class ApiErrorData
{
    [JsonPropertyName("error")] public string Error { get; set; } = "UNKNOWN";
    [JsonPropertyName("message")] public string Message { get; set; } = "未知错误";
}

public sealed class UserInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("plan")] public string Plan { get; set; } = "free";
    [JsonPropertyName("level")] public MembershipLevel? Level { get; set; }
}

public sealed class MembershipLevel
{
    [JsonPropertyName("code")] public string Code { get; set; } = "free";
    [JsonPropertyName("name")] public string Name { get; set; } = "免费版";
    [JsonPropertyName("price_month")] public double PriceMonth { get; set; }
    [JsonPropertyName("max_devices")] public int MaxDevices { get; set; } = 5;
    [JsonPropertyName("max_networks")] public int MaxNetworks { get; set; } = 3;
    [JsonPropertyName("relay_gb")] public int RelayGb { get; set; } = 1;
    [JsonPropertyName("expire_at")] public string? ExpireAt { get; set; }
}

public sealed class DeviceInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("device_uid")] public string DeviceUid { get; set; } = "";
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
}

public sealed class NetworkSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    [JsonPropertyName("anchor_status")] public string AnchorStatus { get; set; } = "pending";
    [JsonPropertyName("anchor_port")] public int? AnchorPort { get; set; }
    [JsonPropertyName("ipv4")] public string? Ipv4 { get; set; }
    [JsonPropertyName("ipv6")] public string? Ipv6 { get; set; }
    // 当前这台设备在该组网里的固定 IP（列表去重后，ipv4 可能显示的是房主那条）
    [JsonPropertyName("device_ipv4")] public string? DeviceIpv4 { get; set; }
    [JsonPropertyName("device_ipv6")] public string? DeviceIpv6 { get; set; }
    [JsonPropertyName("device_role")] public string? DeviceRole { get; set; }
    [JsonPropertyName("server_host")] public string ServerHost { get; set; } = "";
    [JsonPropertyName("server_location")] public string ServerLocation { get; set; } = "广州";

    [JsonIgnore]
    public string RoleText => Role == "owner" ? "房主" : "成员";

    /// <summary>列表里显示的"我的IP"：优先本机在该组网的固定IP</summary>
    [JsonIgnore]
    public string DisplayIpv4 => string.IsNullOrWhiteSpace(DeviceIpv4) ? (Ipv4 ?? "-") : DeviceIpv4!;
}

public sealed class RoomConfig
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("owner_username")] public string? OwnerUsername { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    [JsonPropertyName("anchor_status")] public string AnchorStatus { get; set; } = "pending";
    [JsonPropertyName("ipv4")] public string? Ipv4 { get; set; }
    [JsonPropertyName("ipv6")] public string? Ipv6 { get; set; }
    [JsonPropertyName("gateway_ipv4")] public string GatewayIpv4 { get; set; } = "";
    [JsonPropertyName("ez_network_name")] public string EzNetworkName { get; set; } = "";
    [JsonPropertyName("ez_secret")] public string EzSecret { get; set; } = "";
    [JsonPropertyName("server_host")] public string ServerHost { get; set; } = "";
    [JsonPropertyName("server_location")] public string ServerLocation { get; set; } = "广州";
    [JsonPropertyName("anchor_port")] public int? AnchorPort { get; set; }

    // 中继额度：P2P 直连不计入，额度用完只影响中继回落
    [JsonPropertyName("relay_used_bytes")] public long RelayUsedBytes { get; set; }
    [JsonPropertyName("relay_quota_gb")] public int RelayQuotaGb { get; set; }
    [JsonPropertyName("relay_blocked")] public bool RelayBlocked { get; set; }
}

public sealed class LoginData
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("user")] public UserInfo User { get; set; } = new();
    [JsonPropertyName("device")] public DeviceInfo? Device { get; set; }
    [JsonPropertyName("devices")] public List<DeviceInfo> Devices { get; set; } = new();
    [JsonPropertyName("max_devices")] public int MaxDevices { get; set; } = 5;
    [JsonPropertyName("traffic_month")] public long TrafficMonth { get; set; }
    [JsonPropertyName("relay_quota_gb")] public int RelayQuotaGb { get; set; }
    [JsonPropertyName("relay_blocked")] public bool RelayBlocked { get; set; }
    [JsonPropertyName("networks")] public List<NetworkSummary> Networks { get; set; } = new();
}

public sealed class NetworkListData
{
    [JsonPropertyName("networks")] public List<NetworkSummary> Networks { get; set; } = new();
}

public sealed class PingData
{
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("service")] public string? Service { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class NetworkMember
{
    [JsonPropertyName("member_id")] public int MemberId { get; set; }
    [JsonPropertyName("user_id")] public int UserId { get; set; }
    [JsonPropertyName("device_id")] public int DeviceId { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    [JsonPropertyName("remark")] public string? Remark { get; set; }
    [JsonPropertyName("v4_addr")] public string? V4Addr { get; set; }
    [JsonPropertyName("v6_addr")] public string? V6Addr { get; set; }
    [JsonPropertyName("joined_at")] public string? JoinedAt { get; set; }
    [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("online")] public bool Online { get; set; }
    /// <summary>手机 / 平板接入（走接入网关，不是 EasyTier 对等节点）</summary>
    [JsonPropertyName("is_phone")] public bool IsPhone { get; set; }
    [JsonPropertyName("rx_total")] public long RxTotal { get; set; }
    [JsonPropertyName("tx_total")] public long TxTotal { get; set; }
}

public sealed class NetworkInfoData
{
    [JsonPropertyName("network")] public NetworkSummary Network { get; set; } = new();
    [JsonPropertyName("members")] public List<NetworkMember> Members { get; set; } = new();
    [JsonPropertyName("my_role")] public string MyRole { get; set; } = "member";
}

public sealed class NodeInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    /// <summary>测延迟用的端口（服务端下发，走 TCP 握手时间，不依赖 ping）</summary>
    [JsonPropertyName("probe_port")] public int ProbePort { get; set; } = 443;
}

public sealed class NodesData
{
    [JsonPropertyName("nodes")] public List<NodeInfo> Nodes { get; set; } = new();
}

public sealed class PublicConfig
{
    [JsonPropertyName("register_email_required")] public bool RegisterEmailRequired { get; set; }
    [JsonPropertyName("smtp_ready")] public bool SmtpReady { get; set; }
    [JsonPropertyName("register_invite_required")] public bool RegisterInviteRequired { get; set; }
    [JsonPropertyName("pay_qrcode")] public string PayQrcode { get; set; } = "";
    [JsonPropertyName("pay_note")] public string PayNote { get; set; } = "";
}

/// <summary>删除组网的临时验证码（network_delete_code 的返回）</summary>
public sealed class DeleteCodeData
{
    [JsonPropertyName("sent")] public bool Sent { get; set; }
    /// <summary>发送到的邮箱（已打码），未绑定邮箱时为空</summary>
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    /// <summary>账号未绑定邮箱时，服务端直接把验证码返回给客户端显示</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("notice")] public string Notice { get; set; } = "";
    [JsonPropertyName("expire_seconds")] public int ExpireSeconds { get; set; }
}

public sealed class PlanListData
{
    [JsonPropertyName("plans")] public List<MembershipLevel> Plans { get; set; } = new();
}

public sealed class OrderInfo
{
    [JsonPropertyName("order_no")] public string OrderNo { get; set; } = "";
    [JsonPropertyName("level_code")] public string LevelCode { get; set; } = "";
    [JsonPropertyName("level_name")] public string LevelName { get; set; } = "";
    [JsonPropertyName("months")] public int Months { get; set; }
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("product_type")] public string ProductType { get; set; } = "plan";
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("paid_at")] public string? PaidAt { get; set; }
    [JsonPropertyName("pay_code_url")] public string? PayCodeUrl { get; set; }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        "paid" => "已支付",
        "cancelled" => "已取消",
        _ => "待支付",
    };

    [JsonIgnore] public string AmountText => "￥" + Amount.ToString("0.00");
    [JsonIgnore]
    public string ItemText => ProductType == "invite" ? "邀请码" : (LevelName == "" ? "套餐" : LevelName);
}

/// <summary>会员页展示用的套餐卡片</summary>
public sealed class PlanCard
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Benefit { get; set; } = "";
    public double PriceMonth { get; set; }
    public string PriceText => PriceMonth > 0 ? $"￥{PriceMonth:0.00}/月" : "免费";
}

public sealed class OrderListData
{
    [JsonPropertyName("orders")] public List<OrderInfo> Orders { get; set; } = new();
}

public sealed class OrderQueryData
{
    [JsonPropertyName("order")] public OrderInfo Order { get; set; } = new();
}

public sealed class OrderCreateData
{
    [JsonPropertyName("order")] public OrderInfo Order { get; set; } = new();
    [JsonPropertyName("pay_qrcode")] public string PayQrcode { get; set; } = "";
    [JsonPropertyName("pay_note")] public string PayNote { get; set; } = "";
    [JsonPropertyName("code_url")] public string CodeUrl { get; set; } = "";
    [JsonPropertyName("pay_channel")] public string PayChannel { get; set; } = "manual";
}

public sealed class InviteBuyData
{
    [JsonPropertyName("order_no")] public string OrderNo { get; set; } = "";
    [JsonPropertyName("guest_token")] public string GuestToken { get; set; } = "";
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("code_url")] public string CodeUrl { get; set; } = "";
    [JsonPropertyName("pay_note")] public string PayNote { get; set; } = "";
}

public sealed class InviteOrderData
{
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("invite_code")] public string? InviteCode { get; set; }
}
