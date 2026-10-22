using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Audit;

public sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }
        var errors = options.ValidationErrors();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
