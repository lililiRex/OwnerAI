using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OwnerAI.Host.Desktop.ViewModels;
using Windows.System;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel { get; }

    public ChatPage()
    {
        ViewModel = App.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        // 新消息添加时滚动到底部
        ViewModel.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && MessageList.Items.Count > 0)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (MessageList.Items.Count > 0)
                        MessageList.ScrollIntoView(MessageList.Items[^1]);
                });
            }
        };

        // 流式输出时保持滚动到底部
        ViewModel.ScrollToBottomRequested += () =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (MessageList.Items.Count > 0)
                    MessageList.ScrollIntoView(MessageList.Items[^1]);
            });
        };

        // 页面加载完成后滚动到底部（历史消息恢复后）
        Loaded += (_, _) =>
        {
            if (MessageList.Items.Count > 0)
                MessageList.ScrollIntoView(MessageList.Items[^1]);
        };
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
        {
            if (ViewModel.SendCommand.CanExecute(null))
            {
                ViewModel.SendCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
