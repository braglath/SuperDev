using Avalonia.Controls;

namespace SuperDev.Views;

public partial class CheckDetailWindow : Window
{
   public CheckDetailWindow()
   {
      InitializeComponent();
      Detail.CloseRequested += (_, _) => Close();
   }
}
