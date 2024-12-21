using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using Microsoft.Internal.VisualStudio.PlatformUI;

namespace UnrealContextMenu
{
    public class BaseClassDialogWindow : System.Windows.Window
    {
        private bool? _dialogResult;

        public readonly BaseClassDialog BaseClassDialog;

        public bool DialogResultValue => _dialogResult ?? false;

        public BaseClassDialogWindow(Dictionary<string, ClassInfo> classHierarchy)
        {
            Title = "My Dialog";
//             Width = 400;
//             Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BaseClassDialog = new BaseClassDialog(classHierarchy);
            Content = BaseClassDialog;

            BaseClassDialog.Finished += OnFinished;
        }

        private void OnFinished(object sender, bool result)
        {
            _dialogResult = result;
            Close();
        }
    }
}
