internal sealed class ResidentApplicationContext : ApplicationContext
{
    private readonly BridgeDashboardForm _form;
    internal ResidentApplicationContext(BridgeDashboardForm form)
    {
        _form = form;
        _form.FormClosed += (_, _) => ExitThread();
        _form.StartResident();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _form.Dispose();
        base.Dispose(disposing);
    }
}
