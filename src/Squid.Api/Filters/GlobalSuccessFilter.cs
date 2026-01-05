using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Squid.Message.Response;

namespace Squid.Api.Filters;

public class GlobalSuccessFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Exception != null)
        {
            return;
        }

        if (context.Result is ObjectResult objectResult)
        {
            var value = objectResult.Value;

            if (value is SquidResponse squidResponse)
            {
                if (squidResponse.Code == default)
                {
                    squidResponse.Code = HttpStatusCode.OK;
                }

                if (string.IsNullOrEmpty(squidResponse.Msg))
                {
                    squidResponse.Msg = "Success";
                }

                return;
            }

            if (value == null)
            {
                context.Result = new OkObjectResult(new SquidResponse
                {
                    Code = HttpStatusCode.OK,
                    Msg = "Success"
                });

                return;
            }

            var dataType = value.GetType();
            var wrapperType = typeof(SquidResponse<>).MakeGenericType(dataType);
            var wrapper = (SquidResponse)Activator.CreateInstance(wrapperType)!;

            wrapper.Code = HttpStatusCode.OK;
            wrapper.Msg = "Success";
            wrapperType.GetProperty("Data")!.SetValue(wrapper, value);

            context.Result = new OkObjectResult(wrapper);
        }
        else if (context.Result is EmptyResult)
        {
            context.Result = new OkObjectResult(new SquidResponse
            {
                Code = HttpStatusCode.OK,
                Msg = "Success"
            });
        }
    }
}
