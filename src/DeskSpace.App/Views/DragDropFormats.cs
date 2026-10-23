using System.Windows;

namespace DeskSpace.App.Views;

/// <summary>拖拽数据格式：在分类、分区、项目池之间传递项目 Id。</summary>
public static class DragDropFormats
{
    /// <summary>自定义数据格式名称。</summary>
    public const string ItemId = "DeskSpaceItemId";

    /// <summary>构造携带项目 Id 的拖拽数据。</summary>
    public static DataObject CreateDataObject(string itemId)
    {
        var data = new DataObject();
        data.SetData(ItemId, itemId);
        return data;
    }

    /// <summary>从拖拽数据中取项目 Id；不是本应用拖来的数据时返回 null。</summary>
    public static string? GetItemId(IDataObject data)
        => data is DataObject obj && obj.GetDataPresent(ItemId) ? obj.GetData(ItemId) as string : null;
}
