using System.ComponentModel;
using System.Reflection;
using Xunit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.Core.Tests;

public partial class ViewModelBaseTests
{
    private partial class FakeViewModel : ViewModelBase
    {
        public bool OperationCalled { get; private set; }

        public void SetErrorPublic(string message) => SetError(message);

        public void ClearErrorPublic() => ClearError();

        public Task ExecuteBusyPublicAsync(Func<Task> operation, string? errorContext = null)
            => ExecuteBusyAsync(operation, errorContext);

        [RelayCommand]
        public async Task FakeOperationAsync()
        {
            OperationCalled = true;
            await Task.Yield();
        }

        [RelayCommand]
        public async Task FailingOperationAsync()
        {
            OperationCalled = true;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void HasError_WhenErrorMessageNull_ReturnsFalse()
    {
        var vm = new FakeViewModel();
        Assert.False(vm.HasError);
    }

    [Fact]
    public void SetError_SetsErrorMessage_And_RaisesHasError()
    {
        var vm = new FakeViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelBase.HasError))
                raised = true;
        };

        vm.SetErrorPublic("fail");

        Assert.Equal("fail", vm.ErrorMessage);
        Assert.True(vm.HasError);
        Assert.True(raised);
    }

    [Fact]
    public void ClearError_Clears_And_RaisesHasError()
    {
        var vm = new FakeViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelBase.HasError))
                raised = true;
        };

        vm.SetErrorPublic("fail");
        vm.ClearErrorPublic();

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasError);
        Assert.True(raised);
    }

    [Fact]
    public async Task ExecuteBusyAsync_SetsIsBusyDuringOperation()
    {
        var vm = new FakeViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModelBase.IsBusy))
                raised = true;
        };

        await vm.ExecuteBusyPublicAsync(() => vm.FakeOperationAsync());

        Assert.False(vm.IsBusy);
        Assert.True(raised);
        Assert.True(vm.OperationCalled);
    }

    [Fact]
    public async Task ExecuteBusyAsync_ReEntrant_ReturnsEarly()
    {
        var vm = new FakeViewModel();
        var tcs = new TaskCompletionSource<bool>();

        var first = vm.ExecuteBusyPublicAsync(async () =>
        {
            await tcs.Task;
        });

        // 并发调用应被跳过
        var second = vm.ExecuteBusyPublicAsync(() => vm.FakeOperationAsync());

        tcs.SetResult(true);
        await first;

        Assert.False(vm.IsBusy);
        Assert.False(vm.OperationCalled); // 第二个被跳过
    }

    [Fact]
    public async Task ExecuteBusyAsync_OnException_SetsError_And_ResetsBusy()
    {
        var vm = new FakeViewModel();

        await vm.ExecuteBusyPublicAsync(() => vm.FailingOperationAsync(), "Ctx");

        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Ctx", vm.ErrorMessage);
        Assert.Contains("boom", vm.ErrorMessage);
    }
}
