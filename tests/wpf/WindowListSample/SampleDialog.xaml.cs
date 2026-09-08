using System.Windows;

namespace WindowListSample
{
    /// <summary>検証用のダイアログ。タイトルと説明文だけを受け取り、閉じるボタンで DialogResult を返す。</summary>
    public partial class SampleDialog : Window
    {
        /// <summary>コンストラクタ。</summary>
        /// <param name="InTitle">ウィンドウタイトル（列挙結果の識別に使う）。</param>
        public SampleDialog(string InTitle)
        {
            InitializeComponent();
            Title = InTitle;
            DescriptionText.Text = InTitle;
        }

        /// <summary>閉じるボタン。DialogResult を true にして閉じる。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnCloseClick(object InSender, RoutedEventArgs InArgs)
        {
            DialogResult = true;
        }
    }
}