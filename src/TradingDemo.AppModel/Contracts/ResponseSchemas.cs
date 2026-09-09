namespace TradingDemo.AppModel.Contracts;

/// <summary>
/// JSON Schemas for the API's responses.
/// </summary>
/// <remarks>
/// <para>These express the <i>shape</i> contract, which typed assertions cannot. A test that
/// checks <c>order.Quantity == 1.5m</c> passes even if the API started returning quantity as
/// the string <c>"1.5"</c>, because the deserialiser will happily coerce it. Every strongly
/// typed client in the estate might break on that change while the suite stays green.</para>
///
/// <para><c>additionalProperties: false</c> is used deliberately. It means a <i>new</i> field
/// fails the test. That is usually the right trade for a demonstration of contract discipline -
/// an unannounced field is a change nobody reviewed - but it is a genuine choice with a cost:
/// on a fast-moving API it produces failures for additive, backwards-compatible changes. On a
/// real product I would set it to false for responses consumed by a mobile client that cannot
/// be updated quickly, and true elsewhere. Stating that reasoning is more useful than silently
/// picking one.</para>
/// </remarks>
public static class ResponseSchemas
{
    public const string Login =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["token", "userId", "username", "expiresAtUtc"],
          "additionalProperties": false,
          "properties": {
            "token":        { "type": "string", "minLength": 1 },
            "userId":       { "type": "integer", "minimum": 1 },
            "username":     { "type": "string", "minLength": 1 },
            "expiresAtUtc": { "type": "string", "format": "date-time" }
          }
        }
        """;

    public const string Account =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["id", "accountNumber", "currency", "balance", "accountType", "username"],
          "additionalProperties": false,
          "properties": {
            "id":            { "type": "integer", "minimum": 1 },
            "accountNumber": { "type": "string", "pattern": "^DEMO-[0-9]{7}$" },
            "currency":      { "type": "string", "minLength": 3, "maxLength": 3 },
            "balance":       { "type": "number" },
            "accountType":   { "type": "string", "enum": ["Demo", "Live"] },
            "username":      { "type": "string", "minLength": 1 }
          }
        }
        """;

    /// <summary>
    /// A single instrument. Note the <c>bid</c>/<c>ask</c>/<c>spread</c> types: a price arriving
    /// as a string is the exact regression this catches, and it is a real failure mode when an
    /// API adds decimal-precision handling.
    /// </summary>
    public const string Instrument =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["symbol", "displayName", "assetClass", "bid", "ask", "spread",
                       "minQuantity", "maxQuantity", "isTradable"],
          "additionalProperties": false,
          "properties": {
            "symbol":      { "type": "string", "minLength": 1 },
            "displayName": { "type": "string", "minLength": 1 },
            "assetClass":  { "type": "string", "enum": ["FX", "Metal", "Index", "Crypto"] },
            "bid":         { "type": "number", "exclusiveMinimum": 0 },
            "ask":         { "type": "number", "exclusiveMinimum": 0 },
            "spread":      { "type": "number", "minimum": 0 },
            "minQuantity": { "type": "number", "exclusiveMinimum": 0 },
            "maxQuantity": { "type": "number", "exclusiveMinimum": 0 },
            "isTradable":  { "type": "boolean" }
          }
        }
        """;

    public const string InstrumentList =
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "array",
          "minItems": 1,
          "items": {{Instrument}}
        }
        """;

    public const string Order =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["orderReference", "symbol", "side", "orderType", "quantity",
                       "limitPrice", "status", "createdAtUtc"],
          "additionalProperties": false,
          "properties": {
            "orderReference": { "type": "string", "pattern": "^ORD-[0-9]{8}-[A-Z0-9]+$" },
            "symbol":         { "type": "string", "minLength": 1 },
            "side":           { "type": "string", "enum": ["Buy", "Sell"] },
            "orderType":      { "type": "string", "enum": ["Market", "Limit"] },
            "quantity":       { "type": "number", "exclusiveMinimum": 0 },
            "limitPrice":     { "type": ["number", "null"] },
            "status":         { "type": "string", "enum": ["Pending", "Filled", "Rejected", "Cancelled"] },
            "createdAtUtc":   { "type": "string", "format": "date-time" }
          }
        }
        """;

    public const string OrderPage =
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["items", "page", "pageSize", "totalCount", "totalPages"],
          "additionalProperties": false,
          "properties": {
            "items":      { "type": "array", "items": {{Order}} },
            "page":       { "type": "integer", "minimum": 1 },
            "pageSize":   { "type": "integer", "minimum": 1 },
            "totalCount": { "type": "integer", "minimum": 0 },
            "totalPages": { "type": "integer", "minimum": 0 }
          }
        }
        """;

    /// <summary>
    /// The error envelope. Worth validating on failure paths: an API whose 422 response shape
    /// drifts breaks every client's error handling, and error handling is the code least likely
    /// to have been exercised manually.
    /// </summary>
    public const string ValidationProblem =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["message", "errors"],
          "additionalProperties": false,
          "properties": {
            "message": { "type": "string", "minLength": 1 },
            "errors": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["field", "reason"],
                "additionalProperties": false,
                "properties": {
                  "field":  { "type": "string", "minLength": 1 },
                  "reason": { "type": "string", "minLength": 1 }
                }
              }
            }
          }
        }
        """;
}
