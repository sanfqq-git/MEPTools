using System.Windows;
using MEPTools.FilterCopy.ViewModels;

namespace MEPTools.FilterCopy.Views
{
    public partial class FeedbackWindow : Window
    {
        public FeedbackWindow(FeedbackViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
