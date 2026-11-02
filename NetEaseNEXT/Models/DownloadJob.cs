using System.ComponentModel;
using System.Text.Json.Serialization;

namespace NetEaseNEXT.Models;

public sealed class DownloadJob : INotifyPropertyChanged
{
    public Track Track { get; set; } = new(0, "", "", "", "", 0);
    public string Quality { get; set; } = "lossless";
    public string Source { get; set; } = "download";
    public string ActualLevel { get; set; } = "";
    public bool AudioReady { get; set; }
    public bool TagsReady { get; set; }
    public string UpgradeFromLevel { get; set; } = "";
    public string Directory { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string PartialPath { get; set; } = "";
    public string ActualQuality { get; set; } = "";
    private string _status = "等待中", _error = "";
    private double _progress;
    public string Status { get => _status; set { if (_status == value) return; _status = value; Notify(nameof(Status)); } }
    public string Error { get => _error; set { if (_error == value) return; _error = value; Notify(nameof(Error)); } }
    public double Progress { get => _progress; set { if (_progress == value) return; _progress = value; Notify(nameof(Progress)); } }
    [JsonIgnore] internal CancellationTokenSource? Cancellation;
    [JsonIgnore] internal Task? Completion;
    [JsonIgnore] public bool Active => Status is "等待中" or "下载中" or "写入信息";
    [JsonIgnore] public bool Finished => Status is "已完成" or "无需升级";
    [JsonIgnore] public string Description => Track.Subtitle + "，目标最高 " + Services.AudioQuality.Name(Quality);
    [JsonIgnore] public string Detail => Error.Length > 0 ? "失败：" + Error : Status + (Progress is > 0 and < 100 ? $"，{Progress:0}%" : "") + (ActualQuality.Length > 0 ? "，" + ActualQuality : "");
    [JsonIgnore] public string Action => Active ? "暂停" : Finished ? "打开文件夹" : Status == "失败" ? "重试" : Status == "已取消" ? "重新下载" : "继续";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string property) { PropertyChanged?.Invoke(this, new(property)); PropertyChanged?.Invoke(this, new(nameof(Detail))); if (property == nameof(Status)) foreach (var name in new[] { nameof(Action), nameof(Active), nameof(Finished) }) PropertyChanged?.Invoke(this, new(name)); }
}
