namespace Scan;

public sealed partial class MainViewModel
{
    private string? lastPrintJob;
    private string mailAttachment = "";
    public string MailAttachment { get => mailAttachment; set { Set(ref mailAttachment, value); Notify(nameof(HasMailAttachment)); } }
    public bool HasMailAttachment => MailAttachment.Length > 0;
    public string? LastPrintJob { get => lastPrintJob; set { Set(ref lastPrintJob, value); Notify(nameof(CanCancelPrint)); } }
    public bool CanCancelPrint => !Busy && LastPrintJob is not null;
}
