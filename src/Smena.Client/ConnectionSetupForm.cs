namespace Smena.Client;

/// <summary>Окно первого запуска: адрес сервера и токен устройства, который выдал root.</summary>
internal sealed class ConnectionSetupForm : Form
{
    private readonly TextBox _serverUrl = new() { Dock = DockStyle.Top, PlaceholderText = "http://192.168.1.10:5080" };
    private readonly TextBox _token = new() { Dock = DockStyle.Top, UseSystemPasswordChar = true };
    private readonly Label _error = new() { Dock = DockStyle.Top, ForeColor = Color.Firebrick, AutoSize = true };

    public ConnectionSetupForm(ConnectionSettings? current)
    {
        Text = "Смена — подключение";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 230);
        Padding = new Padding(16);

        var save = new Button { Text = "Подключить", Dock = DockStyle.Bottom, Height = 34 };
        save.Click += (_, _) => TrySave();
        AcceptButton = save;

        if (current is not null)
        {
            _serverUrl.Text = current.ServerUrl.ToString();
        }

        // Элементы с Dock = Top укладываются в обратном порядке добавления.
        Controls.Add(_error);
        Controls.Add(Spacer());
        Controls.Add(_token);
        Controls.Add(Caption("Токен устройства"));
        Controls.Add(Spacer());
        Controls.Add(_serverUrl);
        Controls.Add(Caption("Адрес сервера"));
        Controls.Add(save);
    }

    /// <summary>Заполнено после успешного сохранения.</summary>
    public ConnectionSettings? Result { get; private set; }

    private void TrySave()
    {
        if (!ConnectionSettings.TryParseServerUrl(_serverUrl.Text, out var url))
        {
            _error.Text = "Адрес сервера должен начинаться с http:// или https://";
            return;
        }

        var token = _token.Text.Trim();
        if (token.Length < ConnectionSettings.MinTokenLength)
        {
            _error.Text = $"Токен устройства не короче {ConnectionSettings.MinTokenLength} символов.";
            return;
        }

        Result = new ConnectionSettings(url, token);
        Result.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true };

    private static Panel Spacer() => new() { Dock = DockStyle.Top, Height = 10 };
}
