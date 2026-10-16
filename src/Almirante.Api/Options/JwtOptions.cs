using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "";
    [Required] public string Audience { get; set; } = "";
    [Required] public string ActiveKeyId { get; set; } = "";
    [Required, MinLength(1)] public Dictionary<string, string> Keys { get; set; } = [];
    [Range(1, 30)] public int AccessTokenMinutes { get; set; } = 10;
    [Range(1, 30)] public int AbsoluteSessionDays { get; set; } = 7;
    [Range(1, 168)] public int RefreshInactivityHours { get; set; } = 24;
}
