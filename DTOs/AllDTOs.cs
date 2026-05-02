namespace SRAAS.Api.DTOs;

// ═══════════════════════════════════════════════════
//  AUTH DTOs
// ═══════════════════════════════════════════════════

public record LoginRequest(string Email, string Password, string OrgSlug);

public record RefreshRequest(string RefreshToken);

public record AuthResponse(string AccessToken, string RefreshToken);

// ═══════════════════════════════════════════════════
//  INVITE DTOs
// ═══════════════════════════════════════════════════

public record CreateInviteRequest(int MaxUses = 1, int ExpiryDays = 7, string InviteType = "multi");

public record JoinRequest(string InviteCode, string OrgSlug, string Name, string Email, string Password);

public record InviteResponse(
    Guid Id,
    string InviteCode,
    string InviteType,
    int MaxUses,
    int UsedCount,
    DateTime ExpiresAt,
    bool IsActive,
    string InviteUrl,
    DateTime CreatedAt
);

// ═══════════════════════════════════════════════════
//  ORG DTOs
// ═══════════════════════════════════════════════════

public record OrgResponse(
    Guid Id,
    string Name,
    string Slug,
    int SeatLimit,
    int SeatsUsed,
    DateTime CreatedAt
);

public record UpdateSeatLimitRequest(int NewSeatLimit);

public record RegisterOrgRequest(
    string OrgName,
    string OrgSlug,
    string AdminName,
    string AdminEmail,
    string AdminPassword
);

// ═══════════════════════════════════════════════════
//  MEMBER DTOs
// ═══════════════════════════════════════════════════

public record MemberResponse(
    Guid Id,
    string Name,
    string Email,
    string Role,
    string Status,
    bool IsActive,
    DateTime JoinedAt
);

// ═══════════════════════════════════════════════════
//  APP DTOs
// ═══════════════════════════════════════════════════

public record AppResponse(
    Guid Id,
    string Name,
    string AppType,
    bool IsActive,
    DateTime CreatedAt
);

// ═══════════════════════════════════════════════════
//  CHANNEL DTOs
// ═══════════════════════════════════════════════════

public record CreateChannelRequest(Guid AppId, string Name, string ChannelType = "general", bool IsPrivate = false);

public record ChannelResponse(
    Guid Id,
    string? Name,
    string ChannelType,
    bool IsPrivate,
    DateTime CreatedAt
);

// ═══════════════════════════════════════════════════
//  MESSAGE DTOs
// ═══════════════════════════════════════════════════

public record SendMessageRequest(string Content, string ContentType = "text", Guid? ReplyToId = null);

public record MessageResponse(
    Guid Id,
    Guid ChannelId,
    Guid? SenderId,
    string? SenderName,
    Guid? ReplyToId,
    string? Content,
    string ContentType,
    bool IsEdited,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<AttachmentResponse>? Attachments,
    List<ReactionResponse>? Reactions,
    object? Metadata = null
);

public record AttachmentResponse(Guid Id, string FileName, string FileType, int? FileSizeKb);

public record ReactionResponse(Guid Id, string Emoji, Guid OrgMemberId, string? MemberName);

// ═══════════════════════════════════════════════════
//  REACTION DTOs
// ═══════════════════════════════════════════════════

public record AddReactionRequest(string Emoji);

// ═══════════════════════════════════════════════════
//  AUDIT LOG DTOs
// ═══════════════════════════════════════════════════

public record AuditLogResponse(
    Guid Id,
    Guid? ActorId,
    string? ActorName,
    string Action,
    string? TargetType,
    Guid? TargetId,
    DateTime CreatedAt
);
