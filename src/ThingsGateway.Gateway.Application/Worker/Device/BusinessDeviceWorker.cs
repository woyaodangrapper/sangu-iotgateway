﻿//------------------------------------------------------------------------------
//  此代码版权声明为全文件覆盖，如有原作者特别声明，会在下方手动补充
//  此代码版权（除特别声明外的代码）归作者本人Diego所有
//  源代码使用协议遵循本仓库的开源协议及附加协议
//  Gitee源代码仓库：https://gitee.com/diego2098/ThingsGateway
//  Github源代码仓库：https://github.com/kimdiego2098/ThingsGateway
//  使用文档：https://diego2098.gitee.io/thingsgateway-docs/
//  QQ群：605534569
//------------------------------------------------------------------------------

using Furion.Logging.Extensions;

using Mapster;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThingsGateway.Gateway.Application;

/// <summary>
/// 业务设备服务
/// </summary>
public class BusinessDeviceWorker : DeviceWorker
{
    public BusinessDeviceWorker(IServiceScopeFactory serviceScopeFactory, IHostApplicationLifetime appLifetime) : base(serviceScopeFactory, appLifetime)
    {
        _logger = _serviceScope.ServiceProvider.GetService<ILoggerFactory>().CreateLogger("业务设备服务");
    }

    #region public 设备创建更新结束

    private EasyLock publicRestartLock = new();

    public async Task RestartAsync()
    {
        try
        {
            await publicRestartLock.WaitAsync();

            await StopAsync();
            await StartAsync();
        }
        finally
        {
            publicRestartLock.Release();
        }
    }

    /// <summary>
    /// 启动全部设备，如果没有找到设备会创建
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await restartLock.WaitAsync();
            await singleRestartLock.WaitAsync();
            if (ChannelThreads.Count == 0)
            {
                await CreatAllChannelThreadsAsync();
                await ProtectedStarting();
            }
            await StartAllChannelThreadsAsync();
            await ProtectedStarted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动发生错误");
        }
        finally
        {
            singleRestartLock.Release();
            restartLock.Release();
        }
    }

    /// <summary>
    /// 初始化，如果没有找到设备会创建
    /// </summary>
    public async Task CreatThreadsAsync()
    {
        try
        {
            await restartLock.WaitAsync();
            await singleRestartLock.WaitAsync();
            if (ChannelThreads.Count == 0)
            {
                await CreatAllChannelThreadsAsync();
                await ProtectedStarting();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动发生错误");
        }
        finally
        {
            singleRestartLock.Release();
            restartLock.Release();
        }
    }

    /// <summary>
    /// 停止
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            await restartLock.WaitAsync();
            await singleRestartLock.WaitAsync();
            await StopThreadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止错误");
        }
        finally
        {
            singleRestartLock.Release();
            restartLock.Release();
        }
    }

    private async Task StopThreadAsync()
    {
        //取消全部采集线程
        await BeforeRemoveAllChannelThreadAsync();
        //取消其他后台服务
        await ProtectedStoping();
        //停止全部采集线程
        await RemoveAllChannelThreadAsync();
        //停止其他后台服务
        await ProtectedStoped();
        //清空内存列表
        GlobalData.CollectDevices.Clear();
    }

    private async Task CollectDeviceWorker_Starting()
    {
        await CreatThreadsAsync();
    }

    private async Task CollectDeviceWorker_Started()
    {
        await Task.Delay(1000);
        await StartAsync();
    }

    private async Task CollectDeviceWorker_Stoping()
    {
        await StopAsync();
    }

    /// <summary>
    /// 读取数据库，创建全部设备
    /// </summary>
    /// <returns></returns>
    protected async Task CreatAllChannelThreadsAsync()
    {
        if (!_stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("正在获取业务设备组态信息");
            var deviceRunTimes = await _serviceScope.ServiceProvider.GetService<IDeviceService>().GetBusinessDeviceRuntimeAsync();
            _logger.LogInformation("获取业务设备组态信息完成");
            var idSet = deviceRunTimes.ToDictionary(a => a.Id);
            var result = deviceRunTimes.Where(a => !idSet.ContainsKey(a.RedundantDeviceId) && !a.IsRedundant).ToList();
            result.ForEach(collectDeviceRunTime =>
            {
                if (!_stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        DriverBase driverBase = collectDeviceRunTime.CreatDriver(PluginService);
                        GetChannelThread(driverBase);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{collectDeviceRunTime.Name}初始化错误！");
                    }
                }
            });
        }
    }

    #endregion public 设备创建更新结束

    #region 设备信息获取

    /// <summary>
    /// 获取变量业务属性
    /// </summary>
    /// <param name="driverId"></param>
    /// <param name="dependencyProperties"></param>
    /// <returns></returns>
    public List<DependencyProperty> GetVariablePropertys(string pluginName, List<DependencyProperty> dependencyProperties = null)
    {
        var propertys = PluginService.GetVariablePropertyTypes(pluginName).Values.ToList().Adapt<List<DependencyProperty>>();
        dependencyProperties?.ForEach(it =>
        {
            var dependencyProperty = propertys.FirstOrDefault(a => a.Name == it.Name);
            if (dependencyProperty != null && !string.IsNullOrEmpty(it.Value))
            {
                dependencyProperty.Value = it.Value;
            }
        });

        return propertys.ToList();
    }

    #endregion 设备信息获取

    #region worker服务

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var stoppingToken = new CancellationTokenSource();
        _stoppingToken = stoppingToken.Token;
        stoppingToken.Cancel();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _easyLock?.WaitAsync();
        var collectDeviceWorker = WorkerUtil.GetWoker<CollectDeviceWorker>();
        collectDeviceWorker.Starting += CollectDeviceWorker_Starting;
        collectDeviceWorker.Started += CollectDeviceWorker_Started;
        collectDeviceWorker.Stoping += CollectDeviceWorker_Stoping;
        PluginService = _serviceScope.ServiceProvider.GetService<IPluginService>();
        GlobalData = _serviceScope.ServiceProvider.GetService<GlobalData>();
        await WhileExecuteAsync(stoppingToken);
    }

    protected override async Task<IEnumerable<DeviceRunTime>> GetDeviceRunTimeAsync(long deviceId)
    {
        return await _serviceScope.ServiceProvider.GetService<IDeviceService>().GetBusinessDeviceRuntimeAsync(deviceId);
    }

    #endregion worker服务
}