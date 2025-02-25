﻿using Microsoft.Azure.Functions.Worker.Extensions.Abstractions;

[assembly: ExtensionInformation("Element.Azure.WebJobs.Extensions.RoutePriority", "1.1.0")]

namespace Element.Azure.Functions.Worker.Extensions.RoutePriority
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class RoutePriorityAttribute : InputBindingAttribute
    {
    }
}