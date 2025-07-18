using FluentValidation;
using FluentValidation.Results;

namespace Squid.Core.Middlewares.FluentMessageValidator;

public interface IFluentMessageValidator
{
    void ValidateMessage<TMessage>(TMessage message) where TMessage : IMessage;
    Type ForMessageType { get; }
}

public class FluentMessageValidator<T> : AbstractValidator<T>, IFluentMessageValidator where T : class
{
    public void ValidateMessage<TMessage>(TMessage message) where TMessage : IMessage
    {
        var result = Validate(message as T);
        if (!result.IsValid)
        {
            var validationErrors = new List<ValidationFailure>();
            result.Errors.ForEach(e => validationErrors.Add(new ValidationFailure(e.PropertyName, e.ErrorMessage)));

            if (validationErrors.Any())
            {
                throw new ValidationException(validationErrors);
            }
        }
    }

    public Type ForMessageType => typeof(T);
}