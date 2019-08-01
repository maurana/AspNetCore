// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Server.Circuits
{
    internal class CircuitHost : IAsyncDisposable
    {
        private readonly SemaphoreSlim HandlerLock = new SemaphoreSlim(1);
        private readonly IServiceScope _scope;
        private readonly CircuitHandler[] _circuitHandlers;
        private readonly ILogger _logger;
        private bool _initialized;

        /// <summary>
        /// Sets the current <see cref="Circuits.Circuit"/>.
        /// </summary>
        /// <param name="circuitHost">The <see cref="Circuits.Circuit"/>.</param>
        /// <remarks>
        /// Calling <see cref="SetCurrentCircuitHost(CircuitHost)"/> will store related values such as the
        /// <see cref="IJSRuntime"/> and <see cref="Renderer"/>
        /// in the local execution context. Application code should not need to call this method,
        /// it is primarily used by the Server-Side Components infrastructure.
        /// </remarks>
        public static void SetCurrentCircuitHost(CircuitHost circuitHost)
        {
            if (circuitHost is null)
            {
                throw new ArgumentNullException(nameof(circuitHost));
            }

            JSInterop.JSRuntime.SetCurrentJSRuntime(circuitHost.JSRuntime);
            RendererRegistry.SetCurrentRendererRegistry(circuitHost.RendererRegistry);
        }

        public event UnhandledExceptionEventHandler UnhandledException;

        public CircuitHost(
            string circuitId,
            IServiceScope scope,
            CircuitClientProxy client,
            RendererRegistry rendererRegistry,
            RemoteRenderer renderer,
            IReadOnlyList<ComponentDescriptor> descriptors,
            RemoteJSRuntime jsRuntime,
            CircuitHandler[] circuitHandlers,
            ILogger logger)
        {
            CircuitId = circuitId;
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Client = client;
            RendererRegistry = rendererRegistry ?? throw new ArgumentNullException(nameof(rendererRegistry));
            Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            JSRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
            _logger = logger;

            Services = scope.ServiceProvider;

            Circuit = new Circuit(this);
            _circuitHandlers = circuitHandlers;

            Renderer.UnhandledException += Renderer_UnhandledException;
            Renderer.UnhandledSynchronizationException += SynchronizationContext_UnhandledException;
        }

        public string CircuitId { get; }

        public Circuit Circuit { get; }

        public CircuitClientProxy Client { get; set; }

        public RemoteJSRuntime JSRuntime { get; }

        public RemoteRenderer Renderer { get; }

        public RendererRegistry RendererRegistry { get; }

        public IReadOnlyList<ComponentDescriptor> Descriptors { get; }

        public IServiceProvider Services { get; }

        public void SetCircuitUser(ClaimsPrincipal user)
        {
            var authenticationStateProvider = Services.GetService<AuthenticationStateProvider>() as IHostEnvironmentAuthenticationStateProvider;
            if (authenticationStateProvider != null)
            {
                var authenticationState = new AuthenticationState(user);
                authenticationStateProvider.SetAuthenticationState(Task.FromResult(authenticationState));
            }
        }

        internal void SendPendingBatches()
        {
            // Dispatch any buffered renders we accumulated during a disconnect.
            // Note that while the rendering is async, we cannot await it here. The Task returned by ProcessBufferedRenderBatches relies on
            // OnRenderCompleted to be invoked to complete, and SignalR does not allow concurrent hub method invocations.
            _ = Renderer.Dispatcher.InvokeAsync(() => Renderer.ProcessBufferedRenderBatches());
        }

        public async Task EndInvokeJSFromDotNet(long asyncCall, bool succeded, string arguments)
        {
            try
            {
                AssertInitialized();

                await Renderer.Dispatcher.InvokeAsync(() =>
                {
                    SetCurrentCircuitHost(this);
                    if (!succeded)
                    {
                        // We can log the arguments here because it is simply the JS error with the call stack.
                        Log.EndInvokeJSFailed(_logger, asyncCall, arguments);
                    }
                    else
                    {
                        Log.EndInvokeJSSucceeded(_logger, asyncCall);
                    }

                    DotNetDispatcher.EndInvoke(arguments);
                });
            }
            catch (Exception ex)
            {
                Log.EndInvokeDispatchException(_logger, ex);
            }
        }

        public async Task DispatchEvent(string eventDescriptorJson, string eventArgs)
        {
            RendererRegistryEventDispatcher.BrowserEventDescriptor eventDescriptor = null;
            try
            {
                AssertInitialized();
                eventDescriptor = ParseEventDescriptor(eventDescriptorJson);
                if (eventDescriptor == null)
                {
                    return;
                }

                await Renderer.Dispatcher.InvokeAsync(() =>
                {
                    SetCurrentCircuitHost(this);
                    return RendererRegistryEventDispatcher.DispatchEvent(eventDescriptor, eventArgs);
                });
            }
            catch (Exception ex)
            {
                Log.DispatchEventFailedToDispatchEvent(_logger, eventDescriptor != null ? eventDescriptor.EventHandlerId.ToString() : null, ex);
                UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
            }
        }

        private RendererRegistryEventDispatcher.BrowserEventDescriptor ParseEventDescriptor(string eventDescriptorJson)
        {
            try
            {
                return JsonSerializer.Deserialize<RendererRegistryEventDispatcher.BrowserEventDescriptor>(
                    eventDescriptorJson,
                    JsonSerializerOptionsProvider.Options);
            }
            catch (Exception ex)
            {
                Log.DispatchEventFailedToParseEventDescriptor(_logger, ex);
                return null;
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await Renderer.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    SetCurrentCircuitHost(this);
                    _initialized = true; // We're ready to accept incoming JSInterop calls from here on

                    await OnCircuitOpenedAsync(cancellationToken);
                    await OnConnectionUpAsync(cancellationToken);

                    // We add the root components *after* the circuit is flagged as open.
                    // That's because AddComponentAsync waits for quiescence, which can take
                    // arbitrarily long. In the meantime we might need to be receiving and
                    // processing incoming JSInterop calls or similar.
                    var count = Descriptors.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var (componentType, domElementSelector) = Descriptors[i];
                        await Renderer.AddComponentAsync(componentType, domElementSelector);
                    }
                }
                catch (Exception ex)
                {
                    // We have to handle all our own errors here, because the upstream caller
                    // has to fire-and-forget this
                    Renderer_UnhandledException(this, ex);
                }
            });
        }

        public async Task BeginInvokeDotNetFromJS(string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson)
        {
            try
            {
                AssertInitialized();
                if (assemblyName == "Microsoft.AspNetCore.Components.Web" && methodIdentifier == "DispatchEvent")
                {
                    Log.DispatchEventTroughJSInterop(_logger);
                    return;
                }

                await Renderer.Dispatcher.InvokeAsync(() =>
                {
                    SetCurrentCircuitHost(this);
                    Log.BeginInvokeDotNet(_logger, callId, assemblyName, methodIdentifier, dotNetObjectId);
                    DotNetDispatcher.BeginInvoke(callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson);
                });
            }
            catch (Exception ex)
            {
                UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
            }
        }

        public async Task OnLocationChangedAsync(string uri, bool intercepted)
        {
            try
            {
                AssertInitialized();
                await Renderer.Dispatcher.InvokeAsync(() =>
                {
                    SetCurrentCircuitHost(this);
                    Log.LocationChanging(_logger, CircuitId, uri);
                    var navigationManager = (RemoteNavigationManager)Services.GetRequiredService<NavigationManager>();
                    navigationManager.NotifyLocationChanged(uri, intercepted);
                    Log.LocationChangingSucceeded(_logger, CircuitId, uri);
                });
            }
            catch (Exception ex)
            {
                // It's up to the NavigationManager implementation to validate the URI.
                //
                // Note that it's also possible that setting the URI could cause a failure in code that listens
                // to NavigationManager.LocationChanged.
                //
                // In either case, a well-behaved client will not send invalid URIs, and we don't really
                // want to continue processing with the circuit if setting the URI failed inside application
                // code. The safest thing to do is consider it a critical failure since URI is global state,
                // and a failure means that an update to global state was partially applied.
                Log.LocationChangingFailed(_logger, CircuitId, uri, ex);
                UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
            }
        }

        private async Task OnCircuitOpenedAsync(CancellationToken cancellationToken)
        {
            Log.CircuitOpened(_logger, Circuit.Id);

            for (var i = 0; i < _circuitHandlers.Length; i++)
            {
                var circuitHandler = _circuitHandlers[i];
                try
                {
                    await circuitHandler.OnCircuitOpenedAsync(Circuit, cancellationToken);
                }
                catch (Exception ex)
                {
                    OnHandlerError(circuitHandler, nameof(CircuitHandler.OnCircuitOpenedAsync), ex);
                }
            }
        }

        public async Task OnConnectionUpAsync(CancellationToken cancellationToken)
        {
            Log.ConnectionUp(_logger, Circuit.Id, Client.ConnectionId);

            try
            {
                await HandlerLock.WaitAsync(cancellationToken);

                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    var circuitHandler = _circuitHandlers[i];
                    try
                    {
                        await circuitHandler.OnConnectionUpAsync(Circuit, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        OnHandlerError(circuitHandler, nameof(CircuitHandler.OnConnectionUpAsync), ex);
                    }
                }
            }
            finally
            {
                HandlerLock.Release();
            }
        }

        public async Task OnConnectionDownAsync(CancellationToken cancellationToken)
        {
            Log.ConnectionDown(_logger, Circuit.Id, Client.ConnectionId);

            try
            {
                await HandlerLock.WaitAsync(cancellationToken);

                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    var circuitHandler = _circuitHandlers[i];
                    try
                    {
                        await circuitHandler.OnConnectionDownAsync(Circuit, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        OnHandlerError(circuitHandler, nameof(CircuitHandler.OnConnectionDownAsync), ex);
                    }
                }
            }
            finally
            {
                HandlerLock.Release();
            }
        }

        protected virtual void OnHandlerError(CircuitHandler circuitHandler, string handlerMethod, Exception ex)
        {
            Log.UnhandledExceptionInvokingCircuitHandler(_logger, circuitHandler, handlerMethod, ex);
        }

        private async Task OnCircuitDownAsync()
        {
            Log.CircuitClosed(_logger, Circuit.Id);

            for (var i = 0; i < _circuitHandlers.Length; i++)
            {
                var circuitHandler = _circuitHandlers[i];
                try
                {
                    await circuitHandler.OnCircuitClosedAsync(Circuit, default);
                }
                catch (Exception ex)
                {
                    OnHandlerError(circuitHandler, nameof(CircuitHandler.OnCircuitClosedAsync), ex);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Log.DisposingCircuit(_logger, CircuitId);

            await Renderer.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await OnConnectionDownAsync(CancellationToken.None);
                    await OnCircuitDownAsync();
                }
                finally
                {
                    Renderer.Dispose();
                    _scope.Dispose();
                }
            });
        }

        private void AssertInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Circuit is being invoked prior to initialization.");
            }
        }

        private void Renderer_UnhandledException(object sender, Exception e)
        {
            UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(e, isTerminating: false));
        }

        private void SynchronizationContext_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            UnhandledException?.Invoke(this, e);
        }

        private static class Log
        {
            private static readonly Action<ILogger, Type, string, string, Exception> _unhandledExceptionInvokingCircuitHandler;
            private static readonly Action<ILogger, string, Exception> _disposingCircuit;
            private static readonly Action<ILogger, string, Exception> _onCircuitOpened;
            private static readonly Action<ILogger, string, string, Exception> _onConnectionUp;
            private static readonly Action<ILogger, string, string, Exception> _onConnectionDown;
            private static readonly Action<ILogger, string, Exception> _onCircuitClosed;
            private static readonly Action<ILogger, string, string, string, Exception> _beginInvokeDotNetStatic;
            private static readonly Action<ILogger, string, long, string, Exception> _beginInvokeDotNetInstance;
            private static readonly Action<ILogger, Exception> _endInvokeDispatchException;
            private static readonly Action<ILogger, long, string, Exception> _endInvokeJSFailed;
            private static readonly Action<ILogger, long, Exception> _endInvokeJSSucceeded;
            private static readonly Action<ILogger, Exception> _dispatchEventFailedToParseEventDescriptor;
            private static readonly Action<ILogger, string, Exception> _dispatchEventFailedToDispatchEvent;
            private static readonly Action<ILogger, Exception> _dispatchEventThroughJSInterop;
            private static readonly Action<ILogger, string, string, Exception> _locationChanging;
            private static readonly Action<ILogger, string, string, Exception> _locationChangingSucceeded;
            private static readonly Action<ILogger, string, string, Exception> _locationChangingFailed;

            private static class EventIds
            {
                public static readonly EventId ExceptionInvokingCircuitHandlerMethod = new EventId(100, "ExceptionInvokingCircuitHandlerMethod");
                public static readonly EventId DisposingCircuit = new EventId(101, "DisposingCircuitHost");
                public static readonly EventId OnCircuitOpened = new EventId(102, "OnCircuitOpened");
                public static readonly EventId OnConnectionUp = new EventId(103, "OnConnectionUp");
                public static readonly EventId OnConnectionDown = new EventId(104, "OnConnectionDown");
                public static readonly EventId OnCircuitClosed = new EventId(105, "OnCircuitClosed");
                public static readonly EventId InvalidBrowserEventFormat = new EventId(106, "InvalidBrowserEventFormat");
                public static readonly EventId DispatchEventFailedToParseEventDescriptor = new EventId(107, "DispatchEventFailedToParseEventDescriptor");
                public static readonly EventId DispatchEventFailedToDispatchEvent = new EventId(108, "DispatchEventFailedToDispatchEvent");
                public static readonly EventId BeginInvokeDotNet = new EventId(109, "BeginInvokeDotNet");
                public static readonly EventId EndInvokeDispatchException = new EventId(110, "EndInvokeDispatchException");
                public static readonly EventId EndInvokeJSFailed = new EventId(111, "EndInvokeJSFailed");
                public static readonly EventId EndInvokeJSSucceeded = new EventId(112, "EndInvokeJSSucceeded");
                public static readonly EventId DispatchEventThroughJSInterop = new EventId(113, "DispatchEventThroughJSInterop");
                public static readonly EventId LocationChanging = new EventId(114, "LocationChanging");
                public static readonly EventId LocationChangingSucceded = new EventId(115, "LocationChangingSucceeded");
                public static readonly EventId LocationChangingFailed = new EventId(116, "LocationChangingFailed");
            }

            static Log()
            {
                _unhandledExceptionInvokingCircuitHandler = LoggerMessage.Define<Type, string, string>(
                    LogLevel.Error,
                    EventIds.ExceptionInvokingCircuitHandlerMethod,
                    "Unhandled error invoking circuit handler type {handlerType}.{handlerMethod}: {Message}");

                _disposingCircuit = LoggerMessage.Define<string>(
                    LogLevel.Debug,
                    EventIds.DisposingCircuit,
                    "Disposing circuit with identifier {CircuitId}");

                _onCircuitOpened = LoggerMessage.Define<string>(
                    LogLevel.Debug,
                    EventIds.OnCircuitOpened,
                    "Opening circuit with id {CircuitId}.");

                _onConnectionUp = LoggerMessage.Define<string, string>(
                    LogLevel.Debug,
                    EventIds.OnConnectionUp,
                    "Circuit id {CircuitId} connected using connection {ConnectionId}.");

                _onConnectionDown = LoggerMessage.Define<string, string>(
                    LogLevel.Debug,
                    EventIds.OnConnectionDown,
                    "Circuit id {CircuitId} disconnected from connection {ConnectionId}.");

                _onCircuitClosed = LoggerMessage.Define<string>(
                   LogLevel.Debug,
                   EventIds.OnCircuitClosed,
                   "Closing circuit with id {CircuitId}.");

                _beginInvokeDotNetStatic = LoggerMessage.Define<string, string, string>(
                    LogLevel.Debug,
                    EventIds.BeginInvokeDotNet,
                    "Invoking static method with identifier '{MethodIdentifier}' on assembly '{Assembly}' with callback id '{CallId}'");

                _beginInvokeDotNetInstance = LoggerMessage.Define<string, long, string>(
                    LogLevel.Debug,
                    EventIds.BeginInvokeDotNet,
                    "Invoking instance method '{MethodIdentifier}' on instance '{DotNetObjectId}' with callback id '{CallId}'");

                _endInvokeDispatchException = LoggerMessage.Define(
                    LogLevel.Debug,
                    EventIds.EndInvokeDispatchException,
                    "There was an error invoking 'Microsoft.JSInterop.DotNetDispatcher.EndInvoke'.");

                _endInvokeJSFailed = LoggerMessage.Define<long, string>(
                    LogLevel.Debug,
                    EventIds.EndInvokeJSFailed,
                    "The JS interop call with callback id '{AsyncCall}' failed with error '{Error}'.");

                _endInvokeJSSucceeded = LoggerMessage.Define<long>(
                    LogLevel.Debug,
                    EventIds.EndInvokeJSSucceeded,
                    "The JS interop call with callback id '{AsyncCall}' succeeded.");

                _dispatchEventFailedToParseEventDescriptor = LoggerMessage.Define(
                    LogLevel.Debug,
                    EventIds.DispatchEventFailedToParseEventDescriptor,
                    "Failed to parse the event descriptor data when trying to dispatch an event.");

                _dispatchEventFailedToDispatchEvent = LoggerMessage.Define<string>(
                    LogLevel.Debug,
                    EventIds.DispatchEventFailedToDispatchEvent,
                    "There was an error dispatching the event '{EventHandlerId}' to the application.");

                _dispatchEventThroughJSInterop = LoggerMessage.Define(
                    LogLevel.Debug,
                    EventIds.DispatchEventThroughJSInterop,
                    "There was an intent to dispatch a browser event through JS interop.");

                _locationChanging = LoggerMessage.Define<string, string>(
                    LogLevel.Debug,
                    EventIds.LocationChanging,
                    "Location changing to {URI} in {CircuitId}.");

                _locationChangingSucceeded = LoggerMessage.Define<string, string>(
                    LogLevel.Debug,
                    EventIds.LocationChangingSucceded,
                    "Location changing to {URI} in {CircuitId} succeded.");

                _locationChangingFailed = LoggerMessage.Define<string, string>(
                    LogLevel.Debug,
                    EventIds.LocationChangingFailed,
                    "Location changing to {URI} in {CircuitId} failed.");
            }

            public static void UnhandledExceptionInvokingCircuitHandler(ILogger logger, CircuitHandler handler, string handlerMethod, Exception exception)
            {
                _unhandledExceptionInvokingCircuitHandler(
                    logger,
                    handler.GetType(),
                    handlerMethod,
                    exception.Message,
                    exception);
            }

            public static void DisposingCircuit(ILogger logger, string circuitId) => _disposingCircuit(logger, circuitId, null);

            public static void CircuitOpened(ILogger logger, string circuitId) => _onCircuitOpened(logger, circuitId, null);

            public static void ConnectionUp(ILogger logger, string circuitId, string connectionId) =>
                _onConnectionUp(logger, circuitId, connectionId, null);

            public static void ConnectionDown(ILogger logger, string circuitId, string connectionId) =>
                _onConnectionDown(logger, circuitId, connectionId, null);

            public static void CircuitClosed(ILogger logger, string circuitId) =>
                _onCircuitClosed(logger, circuitId, null);

            public static void EndInvokeDispatchException(ILogger logger, Exception ex) => _endInvokeDispatchException(logger, ex);

            public static void EndInvokeJSFailed(ILogger logger, long asyncHandle, string arguments) => _endInvokeJSFailed(logger, asyncHandle, arguments, null);

            public static void EndInvokeJSSucceeded(ILogger logger, long asyncCall) => _endInvokeJSSucceeded(logger, asyncCall, null);

            public static void DispatchEventFailedToParseEventDescriptor(ILogger logger, Exception ex) => _dispatchEventFailedToParseEventDescriptor(logger, ex);

            public static void DispatchEventFailedToDispatchEvent(ILogger logger, string eventHandlerId, Exception ex) => _dispatchEventFailedToDispatchEvent(logger, eventHandlerId ?? "", ex);

            public static void BeginInvokeDotNet(ILogger logger, string callId, string assemblyName, string methodIdentifier, long dotNetObjectId)
            {
                if (assemblyName != null)
                {
                    _beginInvokeDotNetStatic(logger, methodIdentifier, assemblyName, callId, null);
                }
                else
                {
                    _beginInvokeDotNetInstance(logger, methodIdentifier, dotNetObjectId, callId, null);
                }
            }

            public static void DispatchEventTroughJSInterop(ILogger logger) => _dispatchEventThroughJSInterop(logger, null);

            public static void LocationChanging(ILogger logger, string circuitId, string uri) => _locationChanging(logger, circuitId, uri, null);

            public static void LocationChangingSucceeded(ILogger logger, string circuitId, string uri) => _locationChangingSucceeded(logger, circuitId, uri, null);

            public static void LocationChangingFailed(ILogger logger, string circuitId, string uri, Exception exception) => _locationChangingFailed(logger, circuitId, uri, exception);
        }
    }
}
