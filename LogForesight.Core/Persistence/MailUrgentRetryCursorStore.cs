using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>Small durable cursor for fair keyset paging of old pending urgent mail intents.</summary>
public sealed class MailUrgentRetryCursorStore : JsonBlobSingleton<MailUrgentRetryCursorState>
{
    public const string BlobKey = "mail_urgent_retry_cursor_v1";

    public MailUrgentRetryCursorStore(EfJsonBlobStore blob) : base(blob) { }
}
