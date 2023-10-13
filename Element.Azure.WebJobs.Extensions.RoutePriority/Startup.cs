
using Element.Azure.WebJobs.Extensions.RoutePriority;
using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Description;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.Extensions.Hosting;
using System.Reflection;

[assembly: WebJobsStartup(typeof(Startup))]

namespace Element.Azure.WebJobs.Extensions.RoutePriority
{
    public class Startup : IWebJobsStartup2
    {
        public void Configure(IWebJobsBuilder builder)
        {
            // wont be called
        }

        public void Configure(WebJobsBuilderContext context, IWebJobsBuilder builder)
        {
            builder.AddExtension<RoutePriorityExtensionConfigProvider>();
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    [Binding]
    public class RoutePriorityAttribute : Attribute
    {
    }

    [Extension("RoutePriority")]
    internal class RoutePriorityExtensionConfigProvider : IExtensionConfigProvider
    {
        readonly IWebJobsRouter router;
        readonly IServiceProvider provider;
        bool haveRoutesBeenReordered = false;

        public RoutePriorityExtensionConfigProvider(
            IWebJobsRouter router,
            IServiceProvider provider,
#pragma warning disable CS0618 // Type or member is obsolete
            IApplicationLifetime applicationLifetime
#pragma warning restore CS0618 // Type or member is obsolete
            )
        {
            this.router = router;
            this.provider = provider;

            // This is a failsafe in case the reflection approach fails for some reason.
            // This will always be called after the routes are ready to be reordered, but it is also called after the host has started
            // so in a high-load scenario, a few requests could be handled before the routes are reordered.
            applicationLifetime.ApplicationStarted.Register(() =>
            {
                ReorderRoutes();
            });
        }

        public void Initialize(ExtensionConfigContext context)
        {
            var bindingRule = context.AddBindingRule<RoutePriorityAttribute>();
            bindingRule.BindToInput<object?>(attr => null);

            // The goal here is to get a reference to the ScriptJobHost instance that is created by the runtime so that we can listen for the HostInitialized event.
            // First, we need to find the type 'IScriptJobHost' (it is not in an assembly that we can reference).
            // Once we have it, we need to do the rest of the work in a Task (otherwise, when we call GetService(), it will cause an endless loop).
            // Using the IScriptJobHost type, we can get a reference to the instance from the ServiceProvider.
            // Since we cannot cast the instance to the real type, we then use reflection to add an event handler.
            var iScriptJobHostType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name.EndsWith("IScriptJobHost"));

            Task.Run(() =>
            {
                try
                {
                    if (iScriptJobHostType == null) return;
                    var scriptJobHost = provider.GetService(iScriptJobHostType);
                    var scriptJobHostType = scriptJobHost?.GetType();
                    var eventInfo = scriptJobHostType?.GetEvent("HostInitialized");
                    var methodInfo = this.GetType().GetMethod(nameof(OnScriptJobHostInitialized));
                    if (eventInfo == null || eventInfo.EventHandlerType == null || methodInfo == null) return;
                    var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, methodInfo);
                    eventInfo.AddEventHandler(scriptJobHost, handler);
                }
                catch
                {
                    // if this fails for any reason, suppress it and the ApplicationLifetime.ApplicationStarted fallback will handle reordering the routes
                }
            });
        }

        public void OnScriptJobHostInitialized(object sender, EventArgs args)
        {
            ReorderRoutes();
        }

        public void ReorderRoutes()
        {
            if (haveRoutesBeenReordered) return;

            haveRoutesBeenReordered = true;
            var unorderedRoutes = router.GetRoutes();
            if (unorderedRoutes.Count == 0) return;
            var routePrecedence = Comparer<Route>.Create(RouteComparison);
            var orderedRoutes = unorderedRoutes.OrderBy(id => id, routePrecedence);
            var orderedCollection = new RouteCollection();
            foreach (var route in orderedRoutes)
            {
                orderedCollection.Add(route);
            }
            router.ClearRoutes();
            router.AddFunctionRoutes(orderedCollection, null);

            return;
        }

        static int RouteComparison(Route x, Route y)
        {
            var xTemplate = x.ParsedTemplate;
            var yTemplate = y.ParsedTemplate;

            for (var i = 0; i < xTemplate.Segments.Count; i++)
            {
                if (yTemplate.Segments.Count <= i)
                {
                    return -1;
                }

                var xSegment = xTemplate.Segments[i].Parts[0];
                var ySegment = yTemplate.Segments[i].Parts[0];
                if (!xSegment.IsParameter && ySegment.IsParameter)
                {
                    return -1;
                }
                if (xSegment.IsParameter && !ySegment.IsParameter)
                {
                    return 1;
                }

                if (xSegment.IsParameter)
                {
                    if (xSegment.InlineConstraints.Count() > ySegment.InlineConstraints.Count())
                    {
                        return -1;
                    }
                    else if (xSegment.InlineConstraints.Count() < ySegment.InlineConstraints.Count())
                    {
                        return 1;
                    }
                }
                else
                {
                    var comparison = string.Compare(xSegment.Text, ySegment.Text, StringComparison.OrdinalIgnoreCase);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }
            }
            if (yTemplate.Segments.Count > xTemplate.Segments.Count)
            {
                return 1;
            }
            return 0;
        }
    }

    public static class IWebJobsRouterExtensions
    {
        public static List<Route> GetRoutes(this IWebJobsRouter router)
        {
            var type = typeof(WebJobsRouter);
            var fields = type.GetRuntimeFields();
            var field = fields.First(f => f.Name == "_functionRoutes");
            var functionRoutes = field.GetValue(router) ?? throw new MissingFieldException("_functionRoutes field in WebJobsRouter not found");
            var routeCollection = (RouteCollection)functionRoutes;
            var routes = GetRoutes(routeCollection);
            return routes;
        }

        static List<Route> GetRoutes(RouteCollection collection)
        {
            var routes = new List<Route>();
            for (var i = 0; i < collection.Count; i++)
            {
                if (collection[i] is RouteCollection nestedCollection)
                {
                    routes.AddRange(GetRoutes(nestedCollection));
                    continue;
                }
                routes.Add((Route)collection[i]);
            }
            return routes;
        }
    }
}
