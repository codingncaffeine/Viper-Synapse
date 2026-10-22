using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public class ProtocolTests
{
    [Fact]
    public void A_serial_request_has_the_razer_layout()
    {
        var r = Commands.Request(0x1F, Commands.GetSerial);
        var b = r.Bytes;
        Assert.Equal(RazerReport.Size, b.Length);
        Assert.Equal(0x00, b[0]);   // status: new
        Assert.Equal(0x1F, b[1]);   // transaction id
        Assert.Equal(0x16, b[5]);   // data size
        Assert.Equal(0x00, b[6]);   // command class
        Assert.Equal(0x82, b[7]);   // command id
        // XOR of bytes 2..87: only size, class and id are non-zero.
        Assert.Equal(0x16 ^ 0x00 ^ 0x82, b[88]);
        Assert.True(r.ChecksumValid);
    }

    [Fact]
    public void The_checksum_covers_the_arguments_and_skips_status_and_transaction()
    {
        var a = RazerReport.Create(0x1F, 0x0F, 0x04, 0x01, 0x00, 0xFF);
        var b = RazerReport.Create(0x3F, 0x0F, 0x04, 0x01, 0x00, 0xFF);
        var c = RazerReport.Create(0x1F, 0x0F, 0x04, 0x01, 0x00, 0x80);
        Assert.Equal(a.StoredChecksum, b.StoredChecksum);
        Assert.NotEqual(a.StoredChecksum, c.StoredChecksum);
    }

    [Fact]
    public void An_answer_is_parsed_back()
    {
        var wire = Commands.Request(0x1F, Commands.GetFirmware).Bytes.ToArray();
        wire[0] = (byte)ReportStatus.Successful;
        wire[8] = 1;
        wire[9] = 2;
        var answer = RazerReport.Parse(wire);
        Assert.Equal(ReportStatus.Successful, answer.Status);
        Assert.True(answer.Answers(Commands.Request(0x1F, Commands.GetFirmware)));
        Assert.False(answer.Answers(Commands.Request(0x1F, Commands.GetSerial)));
        Assert.Equal(new byte[] { 1, 2 }, answer.Arguments[..2].ToArray());
    }

    [Fact]
    public void Too_many_arguments_are_refused()
    {
        Assert.Throws<ArgumentException>(() => RazerReport.Create(0x1F, 0, 0, new byte[81], 81));
    }

    [Fact]
    public void A_hidraw_uevent_names_the_device_and_interface()
    {
        var node = HidrawNode.FromUevent("/dev/hidraw17",
            "DRIVER=hid-generic\nHID_ID=0003:00001532:000002EB\nHID_NAME=Razer Razer Tartarus V2 Pro\nHID_PHYS=usb-0000:79:00.0-1.1.2/input3\n");
        Assert.NotNull(node);
        Assert.Equal(0x1532, node.VendorId);
        Assert.Equal(0x02EB, node.ProductId);
        Assert.Equal(3, node.Interface);
        Assert.Equal("usb-0000:79:00.0-1.1.2", node.PortPath);
    }

    [Fact]
    public void A_uevent_without_an_id_is_skipped()
    {
        Assert.Null(HidrawNode.FromUevent("/dev/hidraw0", "DRIVER=hid-generic\n"));
    }
}
