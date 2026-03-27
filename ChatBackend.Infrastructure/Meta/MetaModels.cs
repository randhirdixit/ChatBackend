using System.Text.Json.Serialization;

namespace ChatBackend.Infrastructure.Meta;

// ── Inbound webhook payload from Meta ─────────────────────────────────

public record MetaWebhookPayload(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("entry")]  List<MetaEntry> Entry
);

public record MetaEntry(
    [property: JsonPropertyName("id")]      string Id,
    [property: JsonPropertyName("changes")] List<MetaChange> Changes
);

public record MetaChange(
    [property: JsonPropertyName("value")] MetaChangeValue Value,
    [property: JsonPropertyName("field")] string Field
);

public record MetaChangeValue(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("metadata")]          MetaMetadata Metadata,
    [property: JsonPropertyName("contacts")]          List<MetaContact>? Contacts,
    [property: JsonPropertyName("messages")]          List<MetaMessage>? Messages
);

public record MetaMetadata(
    [property: JsonPropertyName("display_phone_number")] string DisplayPhoneNumber,
    [property: JsonPropertyName("phone_number_id")]      string PhoneNumberId
);

public record MetaContact(
    [property: JsonPropertyName("profile")] MetaProfile Profile,
    [property: JsonPropertyName("wa_id")]   string WaId
);

public record MetaProfile(
    [property: JsonPropertyName("name")] string Name
);

public record MetaMessage(
    [property: JsonPropertyName("from")]      string From,
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("type")]      string Type,
    [property: JsonPropertyName("text")]      MetaTextBody? Text
);

public record MetaTextBody(
    [property: JsonPropertyName("body")] string Body
);

// ── Outbound send-message request to Meta ─────────────────────────────

public record MetaSendMessageRequest(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("to")]                string To,
    [property: JsonPropertyName("type")]              string Type,
    [property: JsonPropertyName("text")]              MetaTextContent Text
);

public record MetaTextContent(
    [property: JsonPropertyName("body")]         string Body,
    [property: JsonPropertyName("preview_url")]  bool PreviewUrl = false
);
