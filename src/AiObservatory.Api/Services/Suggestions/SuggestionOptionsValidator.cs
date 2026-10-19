using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Services.Suggestions;

/// <summary>Fails startup, naming the offending key, when a <c>Suggestions:*</c> value is unusable.</summary>
public sealed class SuggestionOptionsValidator : IValidateOptions<SuggestionOptions>
{
    public ValidateOptionsResult Validate(string? name, SuggestionOptions options)
    {
        var problem = options.Validate();
        return problem is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problem);
    }
}
