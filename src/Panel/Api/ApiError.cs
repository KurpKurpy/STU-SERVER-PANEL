namespace Panel.Api;

public static class ApiError
{
    public static IResult Of(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    // A 400 "validation" error the form shows next to one field.
    public static IResult Field(string field, string message) =>
        Results.Json(new { code = "validation", message, field }, statusCode: 400);
}
