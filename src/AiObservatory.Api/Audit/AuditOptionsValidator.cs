using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed class AuditOptionsValidator(IClock clock) : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }
        var errors = options.ValidationErrors(clock.GetCurrentInstant());
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
