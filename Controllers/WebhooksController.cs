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
        var app = await _db.Apps.FirstOrDefaultAsync(a => a.OrgId == org.Id && a.Name == "WhatsApp Interceptor");
        if (app == null)
        {
            app = new App
            {
                OrgId = org.Id,
                Name = "WhatsApp Interceptor",
                AppType = AppTypeEnum.Chat
            };
            _db.Apps.Add(app);
            await _db.SaveChangesAsync();
            _logger.LogInformation("🆕 Created new App: WhatsApp Interceptor");
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
                // Try to resolve sender_id from the senderName
                Guid? resolvedSenderId = null;
                string sName = (senderName ?? "").ToLower();

                // 1. Handle "You" (always the owner)
                if (sName == "you" || sName == "me")
                {
                    var owner = await _db.OrgMembers
                        .Where(m => m.OrgId == org.Id && (m.Role == MemberRoleEnum.Admin || m.Email.Contains("vishal")))
                        .FirstOrDefaultAsync();
                    resolvedSenderId = owner?.Id;
                }
                else
                {
                    // 2. Smart keyword match (e.g., "Vishal Jio" matches "Vishal Yadav")
                    var member = _db.OrgMembers
                        .Where(m => m.OrgId == org.Id)
                        .AsEnumerable() // Pull for smart matching
                        .FirstOrDefault(m => 
                            m.Name.ToLower() == sName || 
                            sName.Contains(m.Name.ToLower()) ||
                            m.Name.ToLower().Contains(sName.Split(' ')[0])); // Match first name
                    
                    resolvedSenderId = member?.Id;
                }
                
                if (resolvedSenderId != null)
                {
                    _logger.LogInformation("🎯 [MATCHED] Linked '{Name}' to MemberId: {Id}", senderName, resolvedSenderId);
                }

                var metadataJson = JsonSerializer.Serialize(new { sender_name = senderName });
                var message = new Message
                {
                    ChannelId = channel.Id,
                    OrgId = org.Id,
                    SenderId = resolvedSenderId,
                    Content = messageContent,
                    CreatedAt = msgTime,
                    UpdatedAt = msgTime,
                    Metadata = JsonDocument.Parse(metadataJson)
                };
                _db.Messages.Add(message);
                addedCount++;
                _logger.LogInformation("💾 Saved new message to DB: '{Msg}' (SenderId: {SenderId})", messageContent, resolvedSenderId);
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
