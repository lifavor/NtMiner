﻿using NTMiner.MinerStudio;
using NTMiner.MinerStudio.Vms;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NTMiner.Views.MinerStudio.Ucs {
    public partial class LocalIpConfig : UserControl {
        public static void ShowWindow(LocalIpConfigViewModel vm) {
            ContainerWindow.ShowWindow(new Vms.ContainerWindowViewModel {
                Title = "远程管理矿机 IP",
                IconName = "Icon_Ip",
                Width = 450,
                IsMaskTheParent = true,
                FooterVisible = Visibility.Collapsed,
                CloseVisible = Visibility.Visible
            }, ucFactory: (window) => {
                var uc = new LocalIpConfig(vm);
                window.AddCloseWindowOnecePath(uc.Vm.Id);
                uc.ItemsControl.MouseDown += (object sender, MouseButtonEventArgs e) => {
                    if (e.LeftButton == MouseButtonState.Pressed) {
                        window.DragMove();
                    }
                };
                window.AddEventPath<GetLocalIpsResponsedEvent>("收到了获取挖矿端Ip的响应", LogEnum.DevConsole, action: message => {
                    if (message.ClientId != vm.MinerClientVm.ClientId) {
                        return;
                    }
                    vm.LocalIpVms = message.Data.Select(a => new Vms.LocalIpViewModel(a)).ToList();
                }, typeof(LocalIpConfig));
                MinerStudioRoot.MinerStudioService.GetLocalIpsAsync(vm.MinerClientVm);
                return uc;
            }, fixedSize: true);
        }

        public LocalIpConfigViewModel Vm {
            get; private set;
        }

        public LocalIpConfig(LocalIpConfigViewModel vm) {
            this.Vm = vm;
            this.DataContext = vm;
            InitializeComponent();
        }

        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            WpfUtil.ScrollViewer_PreviewMouseDown(sender, e);
        }
    }
}
