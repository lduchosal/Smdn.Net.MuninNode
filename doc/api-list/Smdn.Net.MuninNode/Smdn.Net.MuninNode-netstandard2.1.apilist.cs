// Smdn.Net.MuninNode.dll (Smdn.Net.MuninNode-1.0.0-rc1)
//   Name: Smdn.Net.MuninNode
//   AssemblyVersion: 1.0.0.0
//   InformationalVersion: 1.0.0-rc1+80ea97d27d28014747f5ad977d2e4a58f2f50f3d
//   TargetFramework: .NETStandard,Version=v2.1
//   Configuration: Release
//   Referenced assemblies:
//     Microsoft.Extensions.DependencyInjection.Abstractions, Version=6.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60
//     Microsoft.Extensions.Logging.Abstractions, Version=6.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60
//     Smdn.Fundamental.Encoding.Buffer, Version=3.0.0.0, Culture=neutral
//     Smdn.Fundamental.Exception, Version=3.0.0.0, Culture=neutral
//     System.IO.Pipelines, Version=6.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51
//     netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51
#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Smdn.Net.MuninNode;
using Smdn.Net.MuninPlugin;

namespace Smdn.Net.MuninNode {
  public class LocalNode : NodeBase {
    public LocalNode(IReadOnlyCollection<IPlugin> plugins, int port, IServiceProvider? serviceProvider = null) {}
    public LocalNode(IReadOnlyCollection<IPlugin> plugins, string hostName, int port, IServiceProvider? serviceProvider = null) {}

    public IPEndPoint LocalEndPoint { get; }

    protected override Socket CreateServerSocket() {}
    protected override bool IsClientAcceptable(IPEndPoint remoteEndPoint) {}
  }

  public abstract class NodeBase :
    IAsyncDisposable,
    IDisposable
  {
    public NodeBase(IReadOnlyCollection<IPlugin> plugins, string hostName, ILogger? logger) {}

    public virtual Encoding Encoding { get; }
    public string HostName { get; }
    public virtual Version NodeVersion { get; }
    public IReadOnlyCollection<IPlugin> Plugins { get; }

    public async ValueTask AcceptAsync(bool throwIfCancellationRequested, CancellationToken cancellationToken) {}
    public async ValueTask AcceptSingleSessionAsync(CancellationToken cancellationToken = default) {}
    protected abstract Socket CreateServerSocket();
    protected virtual void Dispose(bool disposing) {}
    public void Dispose() {}
    public async ValueTask DisposeAsync() {}
    protected virtual ValueTask DisposeAsyncCore() {}
    protected abstract bool IsClientAcceptable(IPEndPoint remoteEndPoint);
    public void Start() {}
  }
}

namespace Smdn.Net.MuninPlugin {
  public interface INodeSessionCallback {
    ValueTask ReportSessionClosedAsync(string sessionId, CancellationToken cancellationToken);
    ValueTask ReportSessionStartedAsync(string sessionId, CancellationToken cancellationToken);
  }

  public interface IPlugin {
    IPluginDataSource DataSource { get; }
    PluginGraphAttributes GraphAttributes { get; }
    string Name { get; }
    INodeSessionCallback? SessionCallback { get; }
  }

  public interface IPluginDataSource {
    IReadOnlyCollection<IPluginField> Fields { get; }
  }

  public interface IPluginField {
    PluginFieldAttributes Attributes { get; }
    string Name { get; }

    ValueTask<string> GetFormattedValueStringAsync(CancellationToken cancellationToken);
  }

  public enum PluginFieldGraphStyle : int {
    Area = 1,
    AreaStack = 3,
    Default = 0,
    Line = 100,
    LineStack = 200,
    LineStackWidth1 = 201,
    LineStackWidth2 = 202,
    LineStackWidth3 = 203,
    LineWidth1 = 101,
    LineWidth2 = 102,
    LineWidth3 = 103,
    Stack = 2,
  }

  public class Plugin :
    INodeSessionCallback,
    IPlugin,
    IPluginDataSource
  {
    public Plugin(string name, PluginGraphAttributes graphAttributes, IReadOnlyCollection<IPluginField> fields) {}

    public IReadOnlyCollection<IPluginField> Fields { get; }
    public PluginGraphAttributes GraphAttributes { get; }
    public string Name { get; }
    IPluginDataSource IPlugin.DataSource { get; }
    INodeSessionCallback? IPlugin.SessionCallback { get; }
    IReadOnlyCollection<IPluginField> IPluginDataSource.Fields { get; }

    protected virtual ValueTask ReportSessionClosedAsync(string sessionId, CancellationToken cancellationToken) {}
    protected virtual ValueTask ReportSessionStartedAsync(string sessionId, CancellationToken cancellationToken) {}
    ValueTask INodeSessionCallback.ReportSessionClosedAsync(string sessionId, CancellationToken cancellationToken) {}
    ValueTask INodeSessionCallback.ReportSessionStartedAsync(string sessionId, CancellationToken cancellationToken) {}
  }

  public static class PluginFactory {
    public static IPluginField CreateField(string label, Func<double?> fetchValue) {}
    public static IPluginField CreateField(string label, PluginFieldGraphStyle graphStyle, Func<double?> fetchValue) {}
    public static IPlugin CreatePlugin(string name, PluginGraphAttributes graphAttributes, IReadOnlyCollection<IPluginField> fields) {}
    public static IPlugin CreatePlugin(string name, PluginGraphAttributes graphAttributes, IReadOnlyCollection<PluginFieldBase> fields) {}
    public static IPlugin CreatePlugin(string name, PluginGraphAttributes graphAttributes, PluginFieldBase field) {}
    public static IPlugin CreatePlugin(string name, string fieldLabel, Func<double?> fetchFieldValue, PluginGraphAttributes graphAttributes) {}
    public static IPlugin CreatePlugin(string name, string fieldLabel, PluginFieldGraphStyle fieldGraphStyle, Func<double?> fetchFieldValue, PluginGraphAttributes graphAttributes) {}
  }

  public abstract class PluginFieldBase : IPluginField {
    protected PluginFieldBase(string label, string? name, PluginFieldGraphStyle graphStyle = PluginFieldGraphStyle.Default) {}

    public PluginFieldGraphStyle GraphStyle { get; }
    public string Label { get; }
    public string Name { get; }
    PluginFieldAttributes IPluginField.Attributes { get; }

    protected abstract ValueTask<double?> FetchValueAsync(CancellationToken cancellationToken);
    async ValueTask<string> IPluginField.GetFormattedValueStringAsync(CancellationToken cancellationToken) {}
  }

  public sealed class PluginGraphAttributes {
    public PluginGraphAttributes(string title, string category, string verticalLabel, bool scale, string arguments, TimeSpan updateRate, int? width = null, int? height = null) {}

    public string Arguments { get; }
    public string Category { get; }
    public int? Height { get; }
    public bool Scale { get; }
    public string Title { get; }
    public TimeSpan UpdateRate { get; }
    public string VerticalLabel { get; }
    public int? Width { get; }
  }

  public readonly struct PluginFieldAttributes {
    public PluginFieldAttributes(string label, PluginFieldGraphStyle graphStyle = PluginFieldGraphStyle.Default) {}

    public PluginFieldGraphStyle GraphStyle { get; }
    public string Label { get; }
  }
}
// API list generated by Smdn.Reflection.ReverseGenerating.ListApi.MSBuild.Tasks v1.2.1.0.
// Smdn.Reflection.ReverseGenerating.ListApi.Core v1.2.0.0 (https://github.com/smdn/Smdn.Reflection.ReverseGenerating)
