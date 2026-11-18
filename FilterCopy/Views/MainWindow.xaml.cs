using System.Windows;
using MEPTools.FilterCopy.ViewModels;

namespace MEPTools.FilterCopy.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += (s, ok) =>
            {
                DialogResult = ok;
                Close();
            };
        }
    }
}
