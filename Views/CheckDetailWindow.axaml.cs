using Avalonia.Controls;

namespace SuperDev.Views;

public partial class CheckDetailWindow : Window
{
   public CheckDetailWindow()
   {
      InitializeComponent();
      Detail.ShowCloseButton = false;
      Detail.CloseRequested += (_, _) => Close();
   }
}
