using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MikeSchweitzer.WebSocket.Internal
{
    internal class DotNetWebSocket : IWebSocket
    {
        #region Private Fields
        private readonly Uri _uri;
        private readonly List<string> _subprotocols;
        private readonly Dictionary<string, string> _headers;
        private readonly int _maxReceiveBytes;
        private readonly bool _suppressKeepAlive;

        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;
        private ClientWebSocket _socket;

        private readonly Queue<WebSocketMessage> _outgoingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<string> _incomingErrorMessages = new Queue<string>();
        // temp lists are used to reduce garbage when copying from the locked queues above
        private readonly List<WebSocketMessage> _tempIncomingMessages = new List<WebSocketMessage>();
        private readonly List<string> _tempIncomingErrorMessages = new List<string>();
        #endregion

        #region IWebSocket Events
        public event OpenedHandler Opened;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorHandler Error;
        public event ClosedHandler Closed;
        #endregion

        #region IWebSocket Properties
        public WebSocketState State
        {
            get
            {
                switch (_socket?.State)
                {
                    case System.Net.WebSockets.WebSocketState.Connecting:
                        return WebSocketState.Connecting;

                    case System.Net.WebSockets.WebSocketState.Open:
                        return WebSocketState.Open;

                    case System.Net.WebSockets.WebSocketState.CloseSent:
                    case System.Net.WebSockets.WebSocketState.CloseReceived:
                        return WebSocketState.Closing;

                    case System.Net.WebSockets.WebSocketState.Closed:
                        return WebSocketState.Closed;

                    default:
                        return WebSocketState.Closed;
                }
            }
        }
        #endregion

        #region Ctor/Dtor
        public DotNetWebSocket(
            Uri uri,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers,
            int maxReceiveBytes,
            bool suppressKeepAlive)
        {
            _uri = uri;
            _subprotocols = subprotocols?.ToList();
            _headers = headers?.ToDictionary(pair => pair.Key, pair => pair.Value);
            _maxReceiveBytes = maxReceiveBytes;
            _suppressKeepAlive = suppressKeepAlive;

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationToken = _cancellationTokenSource.Token;
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
        #endregion

        #region IWebSocket Methods
        public void ProcessIncomingMessages()
        {
            lock (_incomingErrorMessages)
            {
                if (_incomingErrorMessages.Count > 0)
                {
                    _tempIncomingErrorMessages.AddRange(_incomingErrorMessages);
                    _incomingErrorMessages.Clear();
                }
            }
            if (_tempIncomingErrorMessages.Count > 0)
            {
                foreach (var message in _tempIncomingErrorMessages)
                    Error?.Invoke(message);
                _tempIncomingErrorMessages.Clear();
            }

            lock (_incomingMessages)
            {
                if (_incomingMessages.Count > 0)
                {
                    _tempIncomingMessages.AddRange(_incomingMessages);
                    _incomingMessages.Clear();
                }
            }
            if (_tempIncomingMessages.Count > 0)
            {
                foreach (var message in _tempIncomingMessages)
                    MessageReceived?.Invoke(message);
                _tempIncomingMessages.Clear();
            }
        }

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            _outgoingMessages.Enqueue(message);
        }

        public async Task ConnectAsync()
        {
            _socket = new ClientWebSocket();

            if (_suppressKeepAlive)
                _socket.Options.KeepAliveInterval = TimeSpan.Zero;

            try
            {
                if (_subprotocols != null)
                {
                    foreach (var subprotocol in _subprotocols)
                        _socket.Options.AddSubProtocol(subprotocol);
                }

                if (_headers != null)
                {
                    foreach (var header in _headers)
                        _socket.Options.SetRequestHeader(header.Key, header.Value);
                }

                await _socket.ConnectAsync(_uri, _cancellationToken);
                Opened?.Invoke();

                await PumpMessagesAsync();
            }
            catch (Exception e)
            {
                if (!_cancellationToken.IsCancellationRequested)
                    Error?.Invoke(e.Message);
            }
            finally
            {
                var closeCode = _socket.CloseStatus == null
                    ? WebSocketCloseCode.Abnormal
                    : WebSocketHelpers.ConvertCloseCode((int)_socket.CloseStatus);
                Closed?.Invoke(closeCode);

                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;
                _socket?.Dispose();
                _socket = null;
            }
        }

        public async Task CloseAsync()
        {
            if (_socket == null)
                return;

            switch (_socket.State)
            {
                case System.Net.WebSockets.WebSocketState.Open:
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;

                case System.Net.WebSockets.WebSocketState.Connecting:
                    _cancellationTokenSource.Cancel();
                    break;
            }
        }

        public void Cancel()
        {
            if (_socket == null)
                return;

            _cancellationTokenSource.Cancel();
        }
        #endregion

        #region Internal Methods
        private async Task PumpMessagesAsync()
        {
            await Task.WhenAll(ThreadedReceiveLoopAsync(), SendLoopAsync());
        }

        private async Task ThreadedReceiveLoopAsync()
        {
            // _socket.ReceiveAsync() is a blocking call, and we don't want to block the main
            // thread, so we need to put it on a separate thread
            await new WaitForThreadPoolThreadStart();

            try
            {
                await ReceiveLoopAsync();
            }
            finally
            {
                // return to the main thread before leaving to guarantee we can make
                // Unity API calls from the call site, once again
                await new WaitForMainThreadUpdate();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new ArraySegment<byte>(new byte[_maxReceiveBytes]);
            while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using (var memoryStream = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    string errorMessage = null;
                    var byteCount = 0;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cancellationToken);

                        byteCount += result.Count;
                        if (byteCount > _maxReceiveBytes)
                        {
                            while (!result.EndOfMessage)
                                result = await _socket.ReceiveAsync(buffer, _cancellationToken);

                            errorMessage = WebSocketHelpers.GetReceiveSizeExceededErrorMessage(byteCount, _maxReceiveBytes);
                            break;
                        }

                        if (result.CloseStatus != null)
                            break;

                        memoryStream.Write(buffer.Array, buffer.Offset, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (errorMessage != null)
                    {
                        lock (_incomingErrorMessages)
                            _incomingErrorMessages.Enqueue(errorMessage);
                    }

                    if (result.CloseStatus != null)
                        break;

                    if (byteCount > _maxReceiveBytes)
                        continue;

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    var bytes = memoryStream.ToArray();
                    var message = result.MessageType == WebSocketMessageType.Binary
                        ? new WebSocketMessage(bytes)
                        : new WebSocketMessage(System.Text.Encoding.UTF8.GetString(bytes));

                    lock (_incomingMessages)
                        _incomingMessages.Enqueue(message);
                }
            }
        }

        private async Task SendLoopAsync()
        {
            while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                while (_outgoingMessages.Count > 0 && _socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var message = _outgoingMessages.Dequeue();

                    var segment = new ArraySegment<byte>(message.Bytes);
                    var type = message.Type == WebSocketDataType.Binary
                        ? WebSocketMessageType.Binary
                        : WebSocketMessageType.Text;
                    await _socket.SendAsync(segment, type, endOfMessage: true, _cancellationToken);
                }

                await Task.Yield();
            }
        }
        #endregion
    }

    // this completes as soon as a ThreadPool thread starts
    internal class WaitForThreadPoolThreadStart
    {
        public ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetAwaiter()
        {
            return Task.Run(() => {}).ConfigureAwait(false).GetAwaiter();
        }
    }

    // this completes as soon as the main thread returns from a coroutine yield
    internal class WaitForMainThreadUpdate : INotifyCompletion
    {
        #region Private Fields
        private Action _continuation;
        #endregion

        #region INotifyCompletion Overrides
        public void OnCompleted(Action continuation)
        {
            _continuation = continuation;
        }
        #endregion

        #region await Support
        public bool IsCompleted { get; private set; }
        public void GetResult() { }
        public WaitForMainThreadUpdate GetAwaiter()
        {
            MainThreadCoroutineRunner.Run(CompleteAfterYieldAsync());
            return this;
        }
        #endregion

        #region Helper Methods
        private IEnumerator CompleteAfterYieldAsync()
        {
            yield return null;
            IsCompleted = true;
            _continuation?.Invoke();
        }
        #endregion
    }

    internal class MainThreadCoroutineRunner : MonoBehaviour
    {
        private static MainThreadCoroutineRunner Instance { get; set; }
        private static SynchronizationContext MainThreadSyncContext { get; set; }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            // this behavior is supposed to be an invisible helper utility to
            // enable awaited tasks on threads to return to the main thread, so
            // we really do not want it to clutter the hierarchy
            gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var go = new GameObject(nameof(MainThreadCoroutineRunner));
            Instance = go.AddComponent<MainThreadCoroutineRunner>();
            MainThreadSyncContext = SynchronizationContext.Current;
        }

        internal static void Run(IEnumerator coroutine)
        {
            MainThreadSyncContext.Post(_ => Instance.StartCoroutine(coroutine), null);
        }
    }
}
