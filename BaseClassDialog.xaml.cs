using Microsoft.Internal.VisualStudio.PlatformUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using MessageBox = System.Windows.MessageBox;

namespace UnrealContextMenu
{
    /// <summary>
    /// Interaction logic for BaseClassDialog.xaml
    /// </summary>
    public partial class BaseClassDialog : UserControl
    {
        private readonly Dictionary<string, ClassInfo> _classHierarchy;
        private List<string> _filteredList;

        public event EventHandler<bool> Finished;

        public string SelectedBaseClass { get; private set; }
        public string NewClassName { get; private set; }

        public BaseClassDialog()
        {
            InitializeComponent();
        }

        public BaseClassDialog(Dictionary<string, ClassInfo> classHierarchy)
        {
            InitializeComponent();
            _classHierarchy = classHierarchy;

            _filteredList = _classHierarchy.Keys.OrderBy(k => k).ToList();
            ClassListBox.ItemsSource = _filteredList;
        }

        private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var filter = FilterTextBox.Text.ToLower();
            _filteredList = _classHierarchy.Keys
                .Where(k => k.ToLower().Contains(filter))
                .OrderBy(k => k)
                .ToList();

            ClassListBox.ItemsSource = _filteredList;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassListBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a base class.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ClassNameTextBox.Text))
            {
                MessageBox.Show("Please enter a valid class name.");
                return;
            }

            SelectedBaseClass = ClassListBox.SelectedItem.ToString();
            NewClassName = ClassNameTextBox.Text.Trim();
            Finished?.Invoke(this, true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Finished?.Invoke(this, false);
        }
    }
}
