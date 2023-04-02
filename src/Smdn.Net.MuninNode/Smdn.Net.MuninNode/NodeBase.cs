// SPDX-FileCopyrightText: 2021 smdn <smdn@smdn.jp>
// SPDX-License-Identifier: MIT

// TODO: use LoggerMessage.Define
#pragma warning disable CA1848 // For improved performance, use the LoggerMessage delegates instead of calling 'LoggerExtensions.LogInformation(ILogger, string?, params object?[])'

#if NET6_0_OR_GREATER
#define SYSTEM_NET_SOCKETS_SOCKET_ACCEPTASYNC_CANCELLATIONTOKEN
#define SYSTEM_NET_SOCKETS_SOCKET_DISCONNECTASYNC_BOOL_CANCELLATIONTOKEN
#endif

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Smdn.Net.MuninPlugin;
#if !SYSTEM_TEXT_ENCODINGEXTENSIONS
using Smdn.Text.Encodings;
#endif

namespace Smdn.Net.MuninNode;

public abstract class NodeBase : IDisposable, IAsyncDisposable {
  private static readonly Version defaultNodeVersion = new(1, 0, 0, 0);

  public IReadOnlyList<Plugin> Plugins { get; }
  public string HostName { get; }

  public virtual Version NodeVersion => defaultNodeVersion;
  public virtual Encoding Encoding => Encoding.Default;

  private readonly ILogger? logger;
  private Socket? server;

  public NodeBase(
    IReadOnlyList<Plugin> plugins,
    string hostName,
    ILogger? logger
  )
  {
    Plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));

    if (hostName == null)
      throw new ArgumentNullException(nameof(hostName));
    if (hostName.Length == 0)
      throw ExceptionUtils.CreateArgumentMustBeNonEmptyString(nameof(hostName));

    HostName = hostName;

    this.logger = logger;
  }

  public void Dispose()
  {
    Dispose(true);
    GC.SuppressFinalize(this);
  }

  public async ValueTask DisposeAsync()
  {
    await DisposeAsyncCore().ConfigureAwait(false);

    Dispose(disposing: false);
    GC.SuppressFinalize(this);
  }

  protected virtual
#if SYSTEM_NET_SOCKETS_SOCKET_DISCONNECTASYNC_BOOL_CANCELLATIONTOKEN
  async
#endif
  ValueTask DisposeAsyncCore()
  {
#if SYSTEM_NET_SOCKETS_SOCKET_DISCONNECTASYNC_BOOL_CANCELLATIONTOKEN
    if (server is not null)
      await server.DisconnectAsync(reuseSocket: false);
#else
    server?.Disconnect(reuseSocket: false);
#endif

    server?.Close();
    server?.Dispose();
    server = null;

#if !SYSTEM_NET_SOCKETS_SOCKET_DISCONNECTASYNC_BOOL_CANCELLATIONTOKEN
    return default;
#endif
  }

  protected virtual void Dispose(bool disposing)
  {
    if (!disposing)
      return;

    server?.Disconnect(reuseSocket: false);
    server?.Close();
    server?.Dispose();
    server = null!;
  }

  protected abstract Socket CreateServerSocket();

  public void Start()
  {
    if (server is not null)
      throw new InvalidOperationException("already started");

    logger?.LogInformation($"starting");

    server = CreateServerSocket() ?? throw new InvalidOperationException("cannot start server");

    logger?.LogInformation("started (end point: {LocalEndPoint})", server.LocalEndPoint);
  }

  protected abstract bool IsClientAcceptable(IPEndPoint remoteEndPoint);

  public async ValueTask AcceptClientAsync(
    CancellationToken cancellationToken = default
  )
  {
    if (server is null)
      throw new InvalidOperationException("not started or already closed");

    logger?.LogInformation("accepting...");

    var client = await server
#if SYSTEM_NET_SOCKETS_SOCKET_ACCEPTASYNC_CANCELLATIONTOKEN
      .AcceptAsync(cancellationToken: cancellationToken)
#else
      .AcceptAsync()
#endif
      .ConfigureAwait(false);

    IPEndPoint? remoteEndPoint = null;

    try {
      cancellationToken.ThrowIfCancellationRequested();

      remoteEndPoint = client.RemoteEndPoint as IPEndPoint;

      if (remoteEndPoint is null) {
        logger?.LogWarning(
          "cannot accept {RemoteEndPoint} ({RemoteEndPointAddressFamily})",
          client.RemoteEndPoint?.ToString() ?? "(null)",
          client.RemoteEndPoint?.AddressFamily
        );
        return;
      }

      if (!IsClientAcceptable(remoteEndPoint)) {
        logger?.LogWarning("access refused: {RemoteEndPoint}", remoteEndPoint);
        return;
      }

      cancellationToken.ThrowIfCancellationRequested();

      logger?.LogInformation("[{RemoteEndPoint}] session started", remoteEndPoint);

      try {
        await SendResponseAsync(
          client,
          Encoding,
          $"# munin node at {HostName}",
          cancellationToken
        ).ConfigureAwait(false);
      }
      catch (SocketException ex) when (
        ex.SocketErrorCode is
          SocketError.Shutdown or // EPIPE (32)
          SocketError.ConnectionAborted or // WSAECONNABORTED (10053)
          SocketError.OperationAborted or // ECANCELED (125)
          SocketError.ConnectionReset // ECONNRESET (104)
      ) {
        logger?.LogWarning(
          "[{RemoteEndPoint}] client closed session while sending banner",
          remoteEndPoint
        );

        return;
      }
      catch (Exception ex) {
        logger?.LogCritical(
          ex,
          "[{RemoteEndPoint}] unexpected exception occured while sending banner",
          remoteEndPoint
        );

        return;
      }

      cancellationToken.ThrowIfCancellationRequested();

      // https://docs.microsoft.com/ja-jp/dotnet/standard/io/pipelines
      var pipe = new Pipe();

      await Task.WhenAll(
        FillAsync(client, remoteEndPoint, pipe.Writer, cancellationToken),
        ReadAsync(client, remoteEndPoint, pipe.Reader, cancellationToken)
      ).ConfigureAwait(false);

      logger?.LogInformation("[{RemoteEndPoint}] session ending", remoteEndPoint);
    }
    finally {
      client.Close();

      logger?.LogInformation("[{RemoteEndPoint}] connection closed", remoteEndPoint);
    }

    async Task FillAsync(
      Socket socket,
      IPEndPoint remoteEndPoint,
      PipeWriter writer,
      CancellationToken cancellationToken
    )
    {
      const int minimumBufferSize = 256;

      for (; ; ) {
        cancellationToken.ThrowIfCancellationRequested();

        var memory = writer.GetMemory(minimumBufferSize);

        try {
          if (!socket.Connected)
            break;

          var bytesRead = await socket.ReceiveAsync(
            buffer: memory,
            socketFlags: SocketFlags.None,
            cancellationToken: cancellationToken
          ).ConfigureAwait(false);

          if (bytesRead == 0)
            break;

          writer.Advance(bytesRead);
        }
        catch (SocketException ex) when (
          ex.SocketErrorCode is
            SocketError.OperationAborted or // ECANCELED (125)
            SocketError.ConnectionReset // ECONNRESET (104)
        ) {
          logger?.LogInformation(
            "[{RemoteEndPoint}] expected socket exception ({NumericSocketErrorCode} {SocketErrorCode})",
            remoteEndPoint,
            (int)ex.SocketErrorCode,
            ex.SocketErrorCode
          );
          break; // expected exception
        }
        catch (ObjectDisposedException) {
          logger?.LogInformation(
            "[{RemoteEndPoint}] socket has been disposed",
            remoteEndPoint
          );
          break; // expected exception
        }
        catch (OperationCanceledException) {
          logger?.LogInformation(
            "[{RemoteEndPoint}] operation canceled",
            remoteEndPoint
          );
          throw;
        }
        catch (Exception ex) {
          logger?.LogCritical(
            ex,
            "[{RemoteEndPoint}] unexpected exception while receiving",
            remoteEndPoint
          );
          break;
        }

        var result = await writer.FlushAsync(
          cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        if (result.IsCompleted)
          break;
      }

      await writer.CompleteAsync().ConfigureAwait(false);
    }

    async Task ReadAsync(
      Socket socket,
      IPEndPoint remoteEndPoint,
      PipeReader reader,
      CancellationToken cancellationToken
    )
    {
      for (; ; ) {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await reader.ReadAsync(
          cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        var buffer = result.Buffer;

        try {
          while (TryReadLine(ref buffer, out var line)) {
            await ProcessCommandAsync(
              client: socket,
              commandLine: line,
              cancellationToken: cancellationToken
            ).ConfigureAwait(false);
          }
        }
        catch (OperationCanceledException) {
          logger?.LogInformation(
            "[{RemoteEndPoint}] operation canceled",
            remoteEndPoint
          );
          throw;
        }
        catch (Exception ex) {
          logger?.LogCritical(
            ex,
            "[{RemoteEndPoint}] unexpected exception while processing command",
            remoteEndPoint
          );

          if (socket.Connected)
            socket.Close();
          break;
        }

        reader.AdvanceTo(buffer.Start, buffer.End);

        if (result.IsCompleted)
          break;
      }
    }

    static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
      var reader = new SequenceReader<byte>(buffer);
      const byte LF = (byte)'\n';

#pragma warning disable SA1003
      if (
#if LANG_VERSION_11_OR_GREATER
        !reader.TryReadTo(out line, delimiter: "\r\n"u8, advancePastDelimiter: true)
#else
        !reader.TryReadTo(out line, delimiter: CRLF.Span, advancePastDelimiter: true)
#endif
        &&
        !reader.TryReadTo(out line, delimiter: LF, advancePastDelimiter: true)
      ) {
        line = default;
        return false;
      }
#pragma warning restore SA1003

#if SYSTEM_BUFFERS_SEQUENCEREADER_UNREADSEQUENCE
      buffer = reader.UnreadSequence;
#else
      buffer = reader.Sequence.Slice(reader.Position);
#endif

      return true;
    }
  }

#if !LANG_VERSION_11_OR_GREATER
  private static readonly ReadOnlyMemory<byte> CRLF = Encoding.ASCII.GetBytes("\r\n");
#endif

  private static bool ExpectCommand(
    ReadOnlySequence<byte> commandLine,
    ReadOnlySpan<byte> expectedCommand,
    out ReadOnlySequence<byte> arguments
  )
  {
    arguments = default;

    var reader = new SequenceReader<byte>(commandLine);

    if (!reader.IsNext(expectedCommand, advancePast: true))
      return false;

    const byte SP = (byte)' ';

    if (reader.Remaining == 0) {
      // <command> <EOL>
      arguments = default;
      return true;
    }
    else if (reader.IsNext(SP, advancePast: true)) {
      // <command> <SP> <arguments> <EOL>
#if SYSTEM_BUFFERS_SEQUENCEREADER_UNREADSEQUENCE
      arguments = reader.UnreadSequence;
#else
      arguments = reader.Sequence.Slice(reader.Position);
#endif
      return true;
    }

    return false;
  }

  private static readonly byte commandQuitShort = (byte)'.';

#if LANG_VERSION_11_OR_GREATER
  private ValueTask ProcessCommandAsync(
    Socket client,
    ReadOnlySequence<byte> commandLine,
    CancellationToken cancellationToken
  )
  {
    if (ExpectCommand(commandLine, "fetch"u8, out var fetchArguments)) {
      return ProcessCommandFetchAsync(client, fetchArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, "nodes"u8, out _)) {
      return ProcessCommandNodesAsync(client, cancellationToken);
    }
    else if (ExpectCommand(commandLine, "list"u8, out var listArguments)) {
      return ProcessCommandListAsync(client, listArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, "config"u8, out var configArguments)) {
      return ProcessCommandConfigAsync(client, configArguments, cancellationToken);
    }
    else if (
      ExpectCommand(commandLine, "quit"u8, out _) ||
      (commandLine.Length == 1 && commandLine.FirstSpan[0] == commandQuitShort)
    ) {
      client.Close();
#if SYSTEM_THREADING_TASKS_VALUETASK_COMPLETEDTASK
      return ValueTask.CompletedTask;
#else
      return default;
#endif
    }
    else if (ExpectCommand(commandLine, "cap"u8, out var capArguments)) {
      return ProcessCommandCapAsync(client, capArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, "version"u8, out _)) {
      return ProcessCommandVersionAsync(client, cancellationToken);
    }
    else {
      return SendResponseAsync(
        client,
        Encoding,
        "# Unknown command. Try cap, list, nodes, config, fetch, version or quit",
        cancellationToken
      );
    }
  }
#else
  private static readonly ReadOnlyMemory<byte> commandFetch       = Encoding.ASCII.GetBytes("fetch");
  private static readonly ReadOnlyMemory<byte> commandNodes       = Encoding.ASCII.GetBytes("nodes");
  private static readonly ReadOnlyMemory<byte> commandList        = Encoding.ASCII.GetBytes("list");
  private static readonly ReadOnlyMemory<byte> commandConfig      = Encoding.ASCII.GetBytes("config");
  private static readonly ReadOnlyMemory<byte> commandQuit        = Encoding.ASCII.GetBytes("quit");
  private static readonly ReadOnlyMemory<byte> commandCap         = Encoding.ASCII.GetBytes("cap");
  private static readonly ReadOnlyMemory<byte> commandVersion     = Encoding.ASCII.GetBytes("version");

  private ValueTask ProcessCommandAsync(
    Socket client,
    ReadOnlySequence<byte> commandLine,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();

    if (ExpectCommand(commandLine, commandFetch.Span, out var fetchArguments)) {
      return ProcessCommandFetchAsync(client, fetchArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, commandNodes.Span, out _)) {
      return ProcessCommandNodesAsync(client, cancellationToken);
    }
    else if (ExpectCommand(commandLine, commandList.Span, out var listArguments)) {
      return ProcessCommandListAsync(client, listArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, commandConfig.Span, out var configArguments)) {
      return ProcessCommandConfigAsync(client, configArguments, cancellationToken);
    }
    else if (
      ExpectCommand(commandLine, commandQuit.Span, out _) ||
      (commandLine.Length == 1 && commandLine.FirstSpan[0] == commandQuitShort)
    ) {
      client.Close();
#if SYSTEM_THREADING_TASKS_VALUETASK_COMPLETEDTASK
      return ValueTask.CompletedTask;
#else
      return default;
#endif
    }
    else if (ExpectCommand(commandLine, commandCap.Span, out var capArguments)) {
      return ProcessCommandCapAsync(client, capArguments, cancellationToken);
    }
    else if (ExpectCommand(commandLine, commandVersion.Span, out _)) {
      return ProcessCommandVersionAsync(client, cancellationToken);
    }
    else {
      return SendResponseAsync(
        client: client,
        encoding: Encoding,
        responseLine: "# Unknown command. Try cap, list, nodes, config, fetch, version or quit",
        cancellationToken: cancellationToken
      );
    }
  }
#endif

#pragma warning disable IDE0230
  private static readonly ReadOnlyMemory<byte> endOfLine = new[] { (byte)'\n' };
#pragma warning restore IDE0230

  private static ValueTask SendResponseAsync(
    Socket client,
    Encoding encoding,
    string responseLine,
    CancellationToken cancellationToken
  )
    => SendResponseAsync(
      client: client,
      encoding: encoding,
      responseLines: Enumerable.Repeat(responseLine, 1),
      cancellationToken: cancellationToken
    );

  private static async ValueTask SendResponseAsync(
    Socket client,
    Encoding encoding,
    IEnumerable<string> responseLines,
    CancellationToken cancellationToken
  )
  {
    if (responseLines == null)
      throw new ArgumentNullException(nameof(responseLines));

    cancellationToken.ThrowIfCancellationRequested();

    foreach (var responseLine in responseLines) {
      var resp = encoding.GetBytes(responseLine);

      await client.SendAsync(
        buffer: resp,
        socketFlags: SocketFlags.None,
        cancellationToken: cancellationToken
      ).ConfigureAwait(false);

      await client.SendAsync(
        buffer: endOfLine,
        socketFlags: SocketFlags.None,
        cancellationToken: cancellationToken
      ).ConfigureAwait(false);
    }
  }

  private ValueTask ProcessCommandNodesAsync(
    Socket client,
    CancellationToken cancellationToken
  )
  {
    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLines: new[] {
        HostName,
        ".",
      },
      cancellationToken: cancellationToken
    );
  }

  private ValueTask ProcessCommandVersionAsync(
    Socket client,
    CancellationToken cancellationToken
  )
  {
    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLine: $"munins node on {HostName} version: {NodeVersion}",
      cancellationToken: cancellationToken
    );
  }

  private ValueTask ProcessCommandCapAsync(
    Socket client,
#pragma warning disable IDE0060
    ReadOnlySequence<byte> arguments,
#pragma warning restore IDE0060
    CancellationToken cancellationToken
  )
  {
    // TODO: multigraph (http://guide.munin-monitoring.org/en/latest/plugin/protocol-multigraph.html)
    // TODO: dirtyconfig (http://guide.munin-monitoring.org/en/latest/plugin/protocol-dirtyconfig.html)
    // XXX: ignores capability arguments
    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLine: "cap",
      cancellationToken: cancellationToken
    );
  }

  private ValueTask ProcessCommandListAsync(
    Socket client,
#pragma warning disable IDE0060
    ReadOnlySequence<byte> arguments,
#pragma warning restore IDE0060
    CancellationToken cancellationToken
  )
  {
    // XXX: ignore [node] arguments
    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLine: string.Join(" ", Plugins.Select(static plugin => plugin.Name)),
      cancellationToken: cancellationToken
    );
  }

  private ValueTask ProcessCommandFetchAsync(
    Socket client,
    ReadOnlySequence<byte> arguments,
    CancellationToken cancellationToken
  )
  {
    var plugin = Plugins.FirstOrDefault(
      plugin => string.Equals(Encoding.GetString(arguments), plugin.Name, StringComparison.Ordinal)
    );

    if (plugin == null) {
      return SendResponseAsync(
        client,
        Encoding,
        new[] {
          "# Unknown service",
          ".",
        },
        cancellationToken
      );
    }

    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLines: plugin
        .FieldConfiguration
        .FetchFields()
        .Select(static f => $"{f.Name}.value {f.FormattedValueString}")
        .Append("."),
      cancellationToken: cancellationToken
    );
  }

  private ValueTask ProcessCommandConfigAsync(
    Socket client,
    ReadOnlySequence<byte> arguments,
    CancellationToken cancellationToken
  )
  {
    var plugin = Plugins.FirstOrDefault(
      plugin => string.Equals(Encoding.GetString(arguments), plugin.Name, StringComparison.Ordinal)
    );

    if (plugin == null) {
      return SendResponseAsync(
        client,
        Encoding,
        new[] {
          "# Unknown service",
          ".",
        },
        cancellationToken
      );
    }

    var responseLines = new List<string>() {
      $"graph_title {plugin.GraphConfiguration.Title}",
      $"graph_category {plugin.GraphConfiguration.Category}",
      $"graph_args {plugin.GraphConfiguration.Arguments}",
      $"graph_scale {(plugin.GraphConfiguration.Scale ? "yes" : "no")}",
      $"graph_vlabel {plugin.GraphConfiguration.VerticalLabel}",
      $"update_rate {(int)plugin.GraphConfiguration.UpdateRate.TotalSeconds}",
    };

    if (plugin.GraphConfiguration.Width.HasValue)
      responseLines.Add($"graph_width {plugin.GraphConfiguration.Width.Value}");
    if (plugin.GraphConfiguration.Height.HasValue)
      responseLines.Add($"graph_height {plugin.GraphConfiguration.Height.Value}");

    foreach (var field in plugin.FieldConfiguration.FetchFields()) {
      responseLines.Add($"{field.Name}.label {field.Label}");

      var draw = field.GraphStyle;

      if (string.IsNullOrEmpty(draw))
        draw = plugin.FieldConfiguration.DefaultGraphStyle;

      if (!string.IsNullOrEmpty(draw))
        responseLines.Add($"{field.Name}.draw {draw}");

      // TODO: add support for "field.warning integer" and "field.warning integer:integer"
#if false
      if (plugin.FieldConfiguration.WarningValue.HasValue) {
        var warningRange = plugin.FieldConfiguration.WarningValue.Value;

        responseLines.Add($"{field.Name}.warning {warningRange.Start}:{warningRange.End}");
      }
      if (plugin.FieldConfiguration.CriticalValue.HasValue) {
        var criticalRange = plugin.FieldConfiguration.CriticalValue.Value;

        responseLines.Add($"{field.ID}.critical {criticalRange.Start}:{criticalRange.End}");
      }
#endif
    }

    responseLines.Add(".");

    return SendResponseAsync(
      client: client,
      encoding: Encoding,
      responseLines: responseLines,
      cancellationToken: cancellationToken
    );
  }
}
