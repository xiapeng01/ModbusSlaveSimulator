using NModbus;
using NModbus.Extensions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace 通用模拟器
{
    /// <summary>
    /// Window1.xaml 的交互逻辑
    /// </summary>
    public partial class Window1 : Window
    {
        StationInformationModel? station;
        public Window1(StationInformationModel _station)
        {
            station = _station;
            InitializeComponent();
            DataContext = station;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void cbRuler_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbRuler.SelectedItem is not RulerModel ruler) return;

            rbReadOnly.IsChecked = ruler.RegisterType == EnumRegisterType.ReadOnly;
            rbReadWrite.IsChecked = ruler.RegisterType == EnumRegisterType.ReadWrite;

            rbBool.IsChecked = ruler.DataType == EnumDataType.Bool;
            rbInt16.IsChecked = ruler.DataType == EnumDataType.Int16;
            rbUInt16.IsChecked = ruler.DataType == EnumDataType.UInt16;
            rbInt32.IsChecked = ruler.DataType == EnumDataType.Int32;
            rbUInt32.IsChecked = ruler.DataType == EnumDataType.UInt32;
            rbFloat.IsChecked = ruler.DataType == EnumDataType.Float;
            rbString.IsChecked = ruler.DataType == EnumDataType.String;

            tbAddress.Text = ruler.StartAddress.ToString();
            // 仅在用户尚未填写内容时预填，避免覆盖已输入的数据
            if (string.IsNullOrWhiteSpace(tbSendContent.Text))
            {
                tbSendContent.Text = string.Join(",", Enumerable.Repeat("0", Math.Max(1, ruler.Length)));
            }
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (station?.Slave is null)
                {
                    MessageBox.Show("站点尚未运行，请先启动网络，并确认该站点已启用。", "提示");
                    return;
                }

                if (!int.TryParse(tbAddress.Text, out var adr))
                {
                    MessageBox.Show("起始地址必须是整数。", "提示");
                    return;
                }
                var str = tbSendContent.Text;                
                str = str.Trim().Trim(new char[] { '[', ']', ',', ';' });//将可能存在的方括号去掉
                string[]? sections = null;
                if (str.Contains(",")) sections = str.Split(",");
                else if (str.Contains(";")) sections = str.Split(";");
                else sections = new string[] { str};
                if (sections is null) throw new Exception("未解析到内容");
                if (rbBool.IsChecked is true)
                {
                    bool[] values = sections.Select(a =>
                    {
                        var s = a.Trim();
                        if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                        throw new FormatException($"无法解析为布尔值：{s}");
                    }).ToArray();
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteBoolR(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteBoolRW(adr, values);
                }
                else if (rbInt16.IsChecked is true)
                {
                    Int16[]? values = null;
                    values = sections.Select(a => (Int16)double.Parse(a)).ToArray();
                    if (values is null) return;
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteInt16R(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteInt16RW(adr, values);
                }
                if (rbUInt16.IsChecked is true)
                {
                    UInt16[]? values = null;
                    values = sections.Select(a => (UInt16)double.Parse(a)).ToArray();
                    if (values is null) return;
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteUInt16R(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteUInt16RW(adr, values);
                }
                if (rbInt32.IsChecked is true)
                {
                    Int32[]? values = null;
                    values = sections.Select(a => (Int32)double.Parse(a)).ToArray();
                    if (values is null) return;
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteInt32R(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteInt32RW(adr, values);
                }
                if (rbUInt32.IsChecked is true)
                {
                    UInt32[]? values = null;
                    values = sections.Select(a => (UInt32)double.Parse(a)).ToArray();
                    if (values is null) return;
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteUInt32R(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteUInt32RW(adr, values);
                }
                if (rbFloat.IsChecked is true)
                {
                    float[]? values = null;
                    values = sections.Select(a => (float)double.Parse(a)).ToArray();
                    if (values is null) return;
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteFloatR(adr, values);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteFloatRW(adr, values);
                }
                if (rbString.IsChecked is true)
                {
                    if (string.IsNullOrWhiteSpace(str))
                    {
                        MessageBox.Show("字符串内容不能为空。", "提示");
                        return;
                    }
                    if (rbReadOnly.IsChecked is true) station?.Slave?.WriteStringR(adr, str);
                    if (rbReadWrite.IsChecked is true) station?.Slave?.WriteStringRW(adr, str);
                }
                MessageBox.Show("成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,"错误");
            }

        }
    }
}
