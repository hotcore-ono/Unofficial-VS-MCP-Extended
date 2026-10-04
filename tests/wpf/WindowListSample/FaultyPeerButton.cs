using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowListSample
{
    /// <summary>
    /// ExceptionMessageFormatter（Tool のエラー文への HRESULT 付加）を実機で踏ませるための検証用ボタン。
    /// IsFaultEnabled が false の間は標準の Button と同じ InvokePattern の契約で動き、true のときだけ
    /// InvokePattern.Invoke が COM 例外（HRESULT = FaultyPeerButtonAutomationPeer.FAULT_HRESULT）を投げる。
    /// Name などのプロパティ取得では投げない（検索走査の途中で投げると、走査側が例外を握りつぶして兄弟要素が見えなくなり、他の検証を壊すため）。
    /// </summary>
    public class FaultyPeerButton : Button
    {
        /// <summary>IsFaultEnabled の依存関係プロパティ。</summary>
        public static readonly DependencyProperty IsFaultEnabledProperty = DependencyProperty.Register(
            nameof(IsFaultEnabled), typeof(bool), typeof(FaultyPeerButton), new PropertyMetadata(false));

        /// <summary>true のときだけ InvokePattern.Invoke が例外を投げる。既定は false（無害）。</summary>
        public bool IsFaultEnabled
        {
            get { return (bool)GetValue(IsFaultEnabledProperty); }
            set { SetValue(IsFaultEnabledProperty, value); }
        }

        /// <summary>UIA の Invoke から物理クリックと同じ Click を発火する（ButtonBase.OnClick は protected のため橋渡しする）。</summary>
        public void RaiseClickFromAutomation()
        {
            OnClick();
        }

        /// <summary>標準の ButtonAutomationPeer の代わりに、例外を投げ得る Peer を返す。</summary>
        /// <returns>このボタン用の AutomationPeer。</returns>
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new FaultyPeerButtonAutomationPeer(this);
        }
    }

    /// <summary>
    /// FaultyPeerButton 用の AutomationPeer。標準の ButtonAutomationPeer と同じ Invoke の契約
    /// （無効なら ElementNotEnabledException、有効なら UI スレッドへ非同期に Click を投げる）を再実装し、
    /// IsFaultEnabled が true のときだけ先頭で COMException を投げる。
    /// </summary>
    public sealed class FaultyPeerButtonAutomationPeer : ButtonAutomationPeer, IInvokeProvider
    {
        /// <summary>
        /// Invoke が失敗するときに COMException へ載せる HRESULT。ITF 領域（0x8004xxxx）の検証用の値で、
        /// 既定の HRESULT（0 / 0x80131500）ではないため、エラー文へ HRESULT が付くことの確認に使える。
        /// </summary>
        public const int FAULT_HRESULT = unchecked((int)0x80041234);

        /// <summary>コンストラクタ。</summary>
        /// <param name="InOwner">この Peer が公開するボタン。</param>
        public FaultyPeerButtonAutomationPeer(FaultyPeerButton InOwner) : base(InOwner)
        {
        }

        /// <summary>
        /// InvokePattern.Invoke の実装（標準 Peer の明示実装を再実装して差し替える）。UIA の呼び出しは UI スレッドへマーシャルされてから届く。
        /// </summary>
        void IInvokeProvider.Invoke()
        {
            FaultyPeerButton TheButton = (FaultyPeerButton)Owner;
            if (TheButton.IsFaultEnabled)
            {
                throw new COMException("FaultyPeerButton: intentional failure from InvokePattern.Invoke", FAULT_HRESULT);
            }
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }
            TheButton.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(TheButton.RaiseClickFromAutomation));
        }
    }
}
