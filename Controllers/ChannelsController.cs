using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SRAAS.Api.Data;
using SRAAS.Api.DTOs;
using SRAAS.Api.Entities;
using SRAAS.Api.Enums;
using SRAAS.Api.Services;

namespace SRAAS.Api.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly SraasDbContext _db;
    private readonly IAuditService _audit;

    public ChannelsController(SraasDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpPost]
    public async Task<IActionResult> CreateChannel([FromBody] CreateChannelRequest request)
    {
        var (memberId, orgId, _) = GetCurrentUser();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == request.AppId && a.OrgId == orgId);
        if (app == null)
            return BadRequest(new { message = "App not found in your organisation." });

        var channelType = Enum.TryParse<ChannelTypeEnum>(request.ChannelType, true, out var ct)
            ? ct : ChannelTypeEnum.General;

        var channel = new Channel
        {
            AppId = request.AppId, OrgId = orgId, Name = request.Name,
            ChannelType = channelType, IsPrivate = request.IsPrivate, CreatedBy = memberId
        };

        _db.Channels.Add(channel);
        _db.ChannelMembers.Add(new ChannelMember { ChannelId = channel.Id, OrgMemberId = memberId });
        await _db.SaveChangesAsync();

        await _audit.LogAsync(orgId, memberId, "channel.created", "channel", channel.Id);

        return Ok(new ChannelResponse(channel.Id, channel.Name,
            channel.ChannelType.ToString().ToLower(), channel.IsPrivate, channel.CreatedAt));
    }

    [HttpGet("{channelId:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid channelId, [FromQuery] Guid? before = null, [FromQuery] int limit = 50)
    {
        var orgId = GetCurrentOrgId();
        limit = Math.Clamp(limit, 1, 100);

        var query = _db.Messages.Where(m => m.ChannelId == channelId && m.OrgId == orgId && !m.IsDeleted);

        if (before.HasValue)
        {
            var cursor = await _db.Messages.FindAsync(before.Value);
            if (cursor != null)
                query = query.Where(m => m.CreatedAt < cursor.CreatedAt);
        }

        var messages = await query
            .OrderByDescending(m => m.CreatedAt).Take(limit)
            .Include(m => m.Sender).Include(m => m.Attachments)
            .Include(m => m.Reactions).ThenInclude(r => r.OrgMember)
            .ToListAsync();

        var result = messages.AsEnumerable().Reverse().Select(m => {
            string? senderName = m.Sender?.Name;
            if (string.IsNullOrEmpty(senderName) && m.Metadata != null)
            {
                if (m.Metadata.RootElement.TryGetProperty("sender_name", out var prop))
                {
                    senderName = prop.GetString();
                }
            }

            // Extract metadata as a dictionary for the frontend
            Dictionary<string, object?>? meta = null;
            if (m.Metadata != null)
            {
                meta = new Dictionary<string, object?>();
                foreach (var p in m.Metadata.RootElement.EnumerateObject())
                {
                    meta[p.Name] = p.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                        System.Text.Json.JsonValueKind.Number => p.Value.GetDouble(),
                        _ => p.Value.ToString()
                    };
                }
            }

            return new MessageResponse(
                m.Id, m.ChannelId, m.SenderId, senderName, m.ReplyToId, m.Content,
                m.ContentType.ToString().ToLower(), m.IsEdited, m.IsDeleted, m.CreatedAt, m.UpdatedAt,
                m.Attachments.Select(a => new AttachmentResponse(a.Id, a.FileName, a.FileType, a.FileSizeKb)).ToList(),
                m.Reactions.Select(r => new ReactionResponse(r.Id, r.Emoji, r.OrgMemberId, r.OrgMember?.Name)).ToList(),
                meta);
        });

        return Ok(result);
    }

    private Guid GetCurrentOrgId() => Guid.Parse(User.FindFirst("org_id")?.Value!);

    private (Guid memberId, Guid orgId, string role) GetCurrentUser()
    {
        var memberId = Guid.Parse(User.FindFirst("sub")?.Value!);
        var orgId = Guid.Parse(User.FindFirst("org_id")?.Value!);
        var role = User.FindFirst("role")?.Value ?? "member";
        return (memberId, orgId, role);
    }
}
