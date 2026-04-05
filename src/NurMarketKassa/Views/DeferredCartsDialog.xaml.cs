using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class DeferredCartsDialog : Window
{
    public IReadOnlyList<DeferredCartEntry> EntriesToRestore { get; private set; } = [];

    public DeferredCartsDialog()
    {
        InitializeComponent();
        ReloadList();
    }

    private void ReloadList()
    {
        CartListBox.Items.Clear();
        foreach (var e in DeferredCartsStore.LoadAll().OrderByDescending(x => x.SavedAt))
            CartListBox.Items.Add(new DeferredCartListRow(e));
    }

    private static int CountLines(string cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            return CartDisplayHelper.EnumerateItems(doc.RootElement).Count();
        }
        catch
        {
            return 0;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = CartListBox.SelectedItems.Cast<DeferredCartListRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Выберите строки в списке.", "Отложенные", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DeferredCartsStore.RemoveIds(rows.Select(r => r.Entry.Id));
        ReloadList();
    }

    private void LoadSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = CartListBox.SelectedItems.Cast<DeferredCartListRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Выберите одну или несколько корзин (Ctrl+щелчок).", "Отложенные",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EntriesToRestore = rows.Select(r => r.Entry).ToList();
        DialogResult = true;
    }

    private sealed class DeferredCartListRow
    {
        internal DeferredCartEntry Entry { get; }

        internal DeferredCartListRow(DeferredCartEntry entry) => Entry = entry;

        public override string ToString()
        {
            var n = CountLines(Entry.CartJson);
            return $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} поз.";
        }
    }
}
