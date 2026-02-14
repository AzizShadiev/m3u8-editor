using System.Collections.Generic;
using System.Windows;

namespace WpfApp1 {
    public partial class RenameGroupWindow : Window {
        public string OldGroup { get; private set; }
        public string NewGroup { get; private set; }

        public RenameGroupWindow(List<string> groups) {
            InitializeComponent();
            ComboGroups.ItemsSource = groups;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) {
            if (ComboGroups.SelectedItem == null || string.IsNullOrWhiteSpace(TxtNewName.Text)) {
                MessageBox.Show("Выберите старую группу и введите новое имя.");
                return;
            }
            OldGroup = ComboGroups.SelectedItem.ToString();
            NewGroup = TxtNewName.Text.Trim();
            DialogResult = true;
        }
    }
}