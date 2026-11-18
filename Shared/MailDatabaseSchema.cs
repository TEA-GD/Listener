using Microsoft.Data.Sqlite;

// Additive and idempotent: all writers use the same mail schema; no mail data is deleted.
public static class MailDatabaseSchema
{
    public static void Initialize(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS email_alerts(
                alert_id TEXT PRIMARY KEY,client_id TEXT NOT NULL,client_label TEXT NOT NULL,account_id TEXT NOT NULL,
                message_key TEXT NOT NULL,message_id TEXT NOT NULL,sender TEXT NOT NULL,sender_address TEXT NOT NULL,sender_domain TEXT NOT NULL,
                recipients_json TEXT NOT NULL,cc_json TEXT NOT NULL,bcc_json TEXT NOT NULL,mailbox_addresses_json TEXT NOT NULL,delivered_to_json TEXT NOT NULL,
                subject TEXT NOT NULL,body_text TEXT NOT NULL,text_parts_json TEXT NOT NULL,attachment_names_json TEXT NOT NULL,body_status TEXT NOT NULL,
                message_date_utc TEXT,received_utc TEXT NOT NULL,UNIQUE(client_id,account_id,message_key)
            );
            CREATE INDEX IF NOT EXISTS idx_email_received ON email_alerts(received_utc);
            CREATE TABLE IF NOT EXISTS email_metadata_alerts(
                alert_id TEXT PRIMARY KEY,client_id TEXT NOT NULL,client_label TEXT NOT NULL,account_id TEXT NOT NULL,
                message_key TEXT NOT NULL,sender TEXT NOT NULL,recipients_json TEXT NOT NULL,
                has_attachments INTEGER NOT NULL CHECK(has_attachments IN (0,1)),attachment_names_json TEXT NOT NULL,
                message_date_utc TEXT NOT NULL,received_utc TEXT NOT NULL,UNIQUE(client_id,account_id,message_key)
            );
            CREATE INDEX IF NOT EXISTS idx_email_metadata_received ON email_metadata_alerts(received_utc);
            """;
        command.ExecuteNonQuery();
    }
}
