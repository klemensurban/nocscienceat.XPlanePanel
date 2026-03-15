namespace nocscienceat.XPlanePanel.Configuration;

public class PanelConfig
{
    public bool Enabled { get; set; }
}

public class SerialPanelConfig : PanelConfig
{
    public string PortName { get; set; } = "disable";
    public int BaudRate { get; set; } = 19200;
}
