using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SRAAS.Api.Data;
using SRAAS.Api.Entities;
using SRAAS.Api.Enums;
using System.Text.Json;

namespace SRAAS.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly SraasDbContext _db;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(SraasDbContext db, ILogger<WebhooksController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public class SyncMessageDto
    {
        public string Chat { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public long Time { get; set; }
        public bool IsOutgoing { get; set; } = false;
        public string? ReplyToContent { get; set; }
    }

    public class SyncRequestDto
    {
        public string? OrgSlug { get; set; }
        public List<SyncMessageDto> Messages { get; set; } = new();
    }

    [HttpPost("whatsapp/sync")]
    [AllowAnonymous]
    public async Task<IActionResult> SyncWhatsAppMessages([FromBody] SyncRequestDto request, [FromHeader(Name = "x-api-key")] string apiKey)
    {
        _logger.LogInformation("==============================================");
        _logger.LogInformation("🔥 [WEBHOOK HIT] Request received at whatsapp/sync");

        // Simple authentication for webhook
        if (apiKey != "SRAAS_SECRET_WEBHOOK_KEY_123")
        {
            _logger.LogWarning("❌ [WEBHOOK REJECTED] Invalid API Key provided: {ApiKey}", apiKey);
            return Unauthorized(new { message = "Invalid API Key" });
        }

        if (request.Messages == null || !request.Messages.Any())
        {
            _logger.LogInformation("⚠️ [WEBHOOK INFO] Payload received, but no messages to sync.");
            return Ok(new { message = "No messages to sync." });
        }

        _logger.LogInformation("✅ [WEBHOOK ACCEPTED] Received {Count} messages. OrgSlug: {OrgSlug}", request.Messages.Count, request.OrgSlug);

        // Identify Organization
        var orgSlug = request.OrgSlug ?? "new-startup";
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Slug == orgSlug);
        
        if (org == null)
        {
            org = await _db.Organizations.FirstOrDefaultAsync();
        }
        if (org == null)
        {
            _logger.LogError("❌ [WEBHOOK ERROR] No organization found in database!");
            return BadRequest(new { message = "No organization found in database." });
        }

        // Find or create the App
        var app = await _db.Apps.FirstOrDefaultAsync(a => a.OrgId == org.Id && (a.Name == "WhatsApp" || a.Name == "WhatsApp Interceptor"));
        if (app == null)
        {
            app = new App
            {
                OrgId = org.Id,
                Name = "WhatsApp",
                AppType = AppTypeEnum.Chat
            };
            _db.Apps.Add(app);
            await _db.SaveChangesAsync();
            _logger.LogInformation("🆕 Created new App: WhatsApp");
        }
        else if (app.Name == "WhatsApp Interceptor")
        {
            app.Name = "WhatsApp";
            await _db.SaveChangesAsync();
        }

        int addedCount = 0;

        foreach (var msg in request.Messages)
        {
            // Normalize chat name to prevent duplicate channels
            var chatName = (msg.Chat ?? "Unknown").Trim();
            var senderName = (msg.Sender ?? "Unknown").Trim();
            var messageContent = (msg.Message ?? "").Trim();

            if (string.IsNullOrEmpty(messageContent)) continue;

            _logger.LogInformation("📩 Processing message for Chat: '{Chat}', Sender: '{Sender}'", chatName, senderName);

            // Find channel — pick the OLDEST one if duplicates exist (fixes race condition)
            var channel = await _db.Channels
                .Where(c => c.AppId == app.Id && c.Name.ToLower() == chatName.ToLower())
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync();

            if (channel == null)
            {
                channel = new Channel
                {
                    AppId = app.Id,
                    OrgId = org.Id,
                    Name = chatName,
                    ChannelType = ChannelTypeEnum.General
                };
                _db.Channels.Add(channel);
                await _db.SaveChangesAsync();
                _logger.LogInformation("🆕 Created new Channel: '{Chat}'", chatName);
            }

            // Create timestamp
            var msgTime = DateTimeOffset.FromUnixTimeMilliseconds(msg.Time).UtcDateTime;

            // Duplicate check: search across ALL channels with same chat name within 2 min window
            var allChannelIds = await _db.Channels
                .Where(c => c.AppId == app.Id && c.Name.ToLower() == chatName.ToLower())
                .Select(c => c.Id)
                .ToListAsync();

            var timeMin = msgTime.AddMinutes(-2);
            var timeMax = msgTime.AddMinutes(2);
            var exists = await _db.Messages.AnyAsync(m => 
                allChannelIds.Contains(m.ChannelId) && 
                m.Content == messageContent && 
                m.CreatedAt >= timeMin && 
                m.CreatedAt <= timeMax);

            if (!exists)
            {
                // Resolve sender_id using is_outgoing flag (most reliable)
                Guid? resolvedSenderId = null;
                
                if (msg.IsOutgoing || senderName.ToLower() == "you" || senderName.ToLower() == "me")
                {
                    // Outgoing = phone owner (admin)
                    var owner = await _db.OrgMembers
                        .Where(m => m.OrgId == org.Id && m.Role == MemberRoleEnum.Admin)
                        .FirstOrDefaultAsync();
                    resolvedSenderId = owner?.Id;
                }
                else
                {
                    // Try keyword match for incoming messages
                    string sName = senderName.ToLower();
                    var member = _db.OrgMembers
                        .Where(m => m.OrgId == org.Id)
                        .AsEnumerable()
                        .FirstOrDefault(m => 
                            m.Name.ToLower() == sName || 
                            sName.Contains(m.Name.ToLower()) ||
                            m.Name.ToLower().Split(' ')[0] == sName.Split(' ')[0]);
                    resolvedSenderId = member?.Id;
                }

                // Resolve reply_to_id from content match
                Guid? replyToId = null;
                if (!string.IsNullOrEmpty(msg.ReplyToContent))
                {
                    var replyTarget = await _db.Messages
                        .Where(m => m.ChannelId == channel.Id && m.Content == msg.ReplyToContent.Trim())
                        .OrderByDescending(m => m.CreatedAt)
                        .FirstOrDefaultAsync();
                    replyToId = replyTarget?.Id;
                }

                var metadataJson = JsonSerializer.Serialize(new { 
                    sender_name = senderName,
                    is_outgoing = msg.IsOutgoing
                });
                var message = new Message
                {
                    ChannelId = channel.Id,
                    OrgId = org.Id,
                    SenderId = resolvedSenderId,
                    ReplyToId = replyToId,
                    Content = messageContent,
                    CreatedAt = msgTime,
                    UpdatedAt = msgTime,
                    Metadata = JsonDocument.Parse(metadataJson)
                };
                _db.Messages.Add(message);
                addedCount++;
                _logger.LogInformation("💾 Saved: '{Msg}' | Outgoing: {Out} | SenderId: {Id} | ReplyTo: {Reply}", messageContent, msg.IsOutgoing, resolvedSenderId, replyToId);
            }
            else
            {
                _logger.LogInformation("⏭️ Skipped duplicate message.");
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("🚀 [WEBHOOK COMPLETE] Successfully saved {Count} new messages.", addedCount);
        _logger.LogInformation("==============================================");

        return Ok(new { message = $"Synced {addedCount} new messages successfully." });
    }
}
