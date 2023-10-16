# HTTP route resolution priority for Isolated Azure Functions
Provides correct and deterministic HTTP route resolution priority for Azure Functions running in Isolated mode.

Potentially ambiguous routes like these will resolve properly when called with a url that could match either one.

```
/api/{userId}
/api/ping
```


## Usage
Add the [Element.Azure.Functions.Worker.Extensions.RoutePriority](https://www.nuget.org/packages/Element.Azure.Functions.Worker.Extensions.RoutePriority) Nuget package to your Isolated Azure Functions project.

**NOTE**: Due to how the Azure Functions metadata generator works, you must actually use the extension in a function declaration. To meet that requirement, you must add the `[RoutePriority]` input binding attribute to **any one** of your HttpTrigger functions (does **not** need to be all of them):

``` csharp
[Function("Ping")]
public HttpResponseData Ping([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData req, [RoutePriority] object ignore)
{
    // ... your code ...
}
```

## How does it work?

See [my original blog post](https://briandunnington.github.io/azure_functions_route_priority) for full details of the problem and how the extension works internally. In the in-process model, the solution from that blog post works great.

However, in the the isolated functions model, the functions host and your functions code run as two separate processes. The functions host recieves the incoming request then communicates with your worker process via gRPC. But the only way to get access to the `IWebJobsRouter` is to do it from the host.

Unfortunately, the code you write in your function app only runs in the worker process and there is no way to 'reach into' the host to modify its behavior.

Except, there is. And it is via Binding Extensions.

Binding extensions are ways to add your own custom trigger types, input bindings, and output bindings. In order to allow them to work, they also have to run in the host. So this solution creates a custom extension that allows code (in this case, code to reorder the route priority order) to run on the host.

One limitation of extensions is that they are only geared towards bindings. As such, your function app code must make use of at least one binding from your extension or else the function extension metadata generator will strip out the extension information from the generated host code. That is why the `[RoutePriority]` input binding attribute needs to be applied _somewhere_ in a function in your function app - it doesnt do anything, other than prevent the rest of the extension code from being complied out.

The actual mechanisms of how extensions work is:
- you add a reference to a worker extension library (in this case: `Element.Azure.Functions.Worker.Extensions.RoutePriority` available via [Nuget](https://www.nuget.org/packages/Element.Azure.Functions.Worker.Extensions.RoutePriority))
- that worker extension library has an `[ExtensionInformation]` attribute that points to *a different* Nuget package that contains the actual logic which will be injected into the host (in this case: `Element.Azure.WebJobs.Extensions.RoutePriority`, which is also available on [Nuget](https://www.nuget.org/packages/Element.Azure.WebJobs.Extensions.RoutePriority) but should not be referenced directly from your function app)
- when you compile your function app, the function metadata generator scans your code for binding extensions, creates a temporary `.csproj` file, and outputs the necessary .dlls and and emits an `extensions.json` file with metadata about your extension and the entry points

You don't _need_ to know any of that in order to take advantage of this package, but it might be interesting to know what is going on under the hood.

### References

Credit goes to [Maarten Balliauw](https://github.com/maartenba) for [documenting how to write custom extensions for isolated functions](https://blog.maartenballiauw.be/post/2021/06/01/custom-bindings-with-azure-functions-dotnet-isolated-worker.html). It was invaluable in understanding how things worked internally.

The [source code for the functions metadata generator](https://github.com/Azure/azure-functions-dotnet-worker/blob/bba8136917f2a60d884387182fca35ed19aaf8e4/sdk/FunctionMetadataLoaderExtension/Startup.cs) (where you can see how items are stripped out if not used) is also interesting reading.

The [source code for the Microsoft-provided extensions](https://github.com/Azure/azure-functions-dotnet-worker/tree/main/extensions) was also a great reference, as was the [Dapr extension source code](https://github.com/Azure/azure-functions-dapr-extension/tree/master/src).
