using System.Linq;
using System.Numerics;
using Content.Client.Pinpointer.UI;
using Content.Client.Resources;
using Content.Client.Stylesheets;
using Content.Client.UserInterface.Controls;
using Content.Client.Viewport;
using Content.Shared.DeviceNetwork;
using Content.Shared.SurveillanceCamera;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Graphics;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.Client.SurveillanceCamera.UI;

[GenerateTypedNameReferences]
public sealed partial class SurveillanceCameraMonitorWindow : FancyWindow
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IResourceCache _resourceCache = default!;

    public event Action<string>? CameraSelected;
    public event Action<string>? SubnetOpened;
    public event Action? CameraRefresh;
    public event Action? SubnetRefresh;
    public event Action? CameraSwitchTimer;
    public event Action? CameraDisconnect;

    private string _currentAddress = string.Empty;
    private bool _isSwitching;
    private readonly IEyeManager _eye;
    private EntityUid? _stationUid;
    private readonly IEntityManager _entManager;
    private readonly FixedEye _defaultEye = new();
    private readonly Dictionary<string, int> _subnetMap = new();
    private Dictionary<string, EntityCoordinates?> _subnetCords = new();

    public SurveillanceCameraMonitorWindow(EntityUid? mapUid)
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        // This could be done better. I don't want to deal with stylesheets at the moment.
        var texture = _resourceCache.GetTexture("/Textures/Interface/Nano/square_black.png");
        var shader = _prototypeManager.Index<ShaderPrototype>("CameraStatic").Instance().Duplicate();

        CameraView.ViewportSize = new Vector2i(500, 500);
        CameraView.Eye = _defaultEye; // sure
        CameraViewBackground.Stretch = TextureRect.StretchMode.Scale;
        CameraViewBackground.Texture = texture;
        CameraViewBackground.ShaderOverride = shader;

        SubnetSelector.OnItemSelected += args =>
        {
            // piss
            SubnetOpened!((string) args.Button.GetItemMetadata(args.Id)!);
        };
        SubnetRefreshButton.OnPressed += _ => SubnetRefresh!();
        CameraRefreshButton.OnPressed += _ => CameraRefresh!();
        CameraDisconnectButton.OnPressed += _ => CameraDisconnect!();
        NavMapScreen.CoordinateClicked += OnCoordinateClicked;

        _eye = IoCManager.Resolve<IEyeManager>();
        _entManager = IoCManager.Resolve<IEntityManager>();
        _stationUid = mapUid;

        if (_entManager.TryGetComponent<TransformComponent>(mapUid, out var xform))
        {
            NavMapScreen.MapUid = xform.GridUid;
        }
        else
        {
            NavMapScreen.Visible = false;
            SetSize = new Vector2(775, 400);
            MinSize = SetSize;
        }
    }

    private void OnCoordinateClicked(EntityCoordinates coordinates)
    {
        string? subnetKey = FindKeyByValue(_subnetCords, coordinates);
        if (subnetKey != null)
        {
            CameraSelected!((string) subnetKey);
        }
    }

    private string? FindKeyByValue(Dictionary<string, EntityCoordinates?> dictionary, EntityCoordinates? value)
    {
        foreach (var pair in dictionary)
        {
            if (pair.Value == value)
            {
                return pair.Key;
            }
        }
        return null;
    }

    public void ShowCameras(Dictionary<string, EntityCoordinates?> camerasCordinates, EntityCoordinates? monitorCoords)
    {
        _subnetCords = camerasCordinates;
        ClearAllCamerasPoint();

        // TODO scroll container
        // TODO filter by name & occupation
        // TODO make each row a xaml-control. Get rid of some of this c# control creation.
        //if (subnets.Count == 0)
        //{
        //    NoServerLabel.Visible = true;
        //    return;
        //}
        //NoServerLabel.Visible = false;

        foreach (var subnet in camerasCordinates)
        {
            if (subnet.Value != null && NavMapScreen.Visible)
            {
                NavMapScreen.TrackedCoordinates.TryAdd(subnet.Value.Value,
                    (true, Color.Red, NavMapControl.ShapeType.Triangle));
            }
        }

        // Show monitor point
        if (monitorCoords != null)
            NavMapScreen.TrackedCoordinates.Add(monitorCoords.Value, (true, StyleNano.PointMagenta, NavMapControl.ShapeType.Circle));
    }


    // The UI class should get the eye from the entity, and then
    // pass it here so that the UI can change its view.
    public void UpdateState(IEye? eye, HashSet<string> subnets, string activeAddress, string activeSubnet, Dictionary<string, string> cameras)
    {
        _currentAddress = activeAddress;
        SetCameraView(eye);

        if (subnets.Count == 0)
        {
            SubnetSelector.AddItem(Loc.GetString("surveillance-camera-monitor-ui-no-subnets"));
            SubnetSelector.Disabled = true;
            return;
        }

        if (SubnetSelector.Disabled && subnets.Count != 0)
        {
            SubnetSelector.Clear();
            SubnetSelector.Disabled = false;
        }

        // That way, we have *a* subnet selected if this is ever opened.
        if (string.IsNullOrEmpty(activeSubnet))
        {
            SubnetOpened!(subnets.First());
            return;
        }

        // if the subnet count is unequal, that means
        // we have to rebuild the subnet selector
        if (SubnetSelector.ItemCount != subnets.Count)
        {
            SubnetSelector.Clear();
            _subnetMap.Clear();

            foreach (var subnet in subnets)
            {
                var id = AddSubnet(subnet);
                _subnetMap.Add(subnet, id);
            }
        }

        if (_subnetMap.TryGetValue(activeSubnet, out var subnetId))
        {
            SubnetSelector.Select(subnetId);
        }
    }


    private void SetCameraView(IEye? eye)
    {
        var eyeChanged = eye != CameraView.Eye || CameraView.Eye == null;
        CameraView.Eye = eye ?? _defaultEye;
        CameraView.Visible = !eyeChanged && !_isSwitching;
        CameraDisconnectButton.Disabled = eye == null;

        if (eye != null)
        {
            if (!eyeChanged)
            {
                return;
            }

            _isSwitching = true;
            CameraViewBackground.Visible = true;
            CameraStatus.Text = Loc.GetString("surveillance-camera-monitor-ui-status",
                ("status", Loc.GetString("surveillance-camera-monitor-ui-status-connecting")),
                ("address", _currentAddress));
            CameraSwitchTimer!();
        }
        else
        {
            CameraViewBackground.Visible = true;
            CameraStatus.Text = Loc.GetString("surveillance-camera-monitor-ui-status-disconnected");
        }
    }

    public void OnSwitchTimerComplete()
    {
        _isSwitching = false;
        CameraView.Visible = CameraView.Eye != _defaultEye;
        CameraViewBackground.Visible = CameraView.Eye == _defaultEye;
        CameraStatus.Text = Loc.GetString("surveillance-camera-monitor-ui-status",
                            ("status", Loc.GetString("surveillance-camera-monitor-ui-status-connected")),
                            ("address", _currentAddress));
    }

    private int AddSubnet(string subnet)
    {
        var name = subnet;
        if (_prototypeManager.TryIndex<DeviceFrequencyPrototype>(subnet, out var frequency))
        {
            name = Loc.GetString(frequency.Name ?? subnet);
        }

        SubnetSelector.AddItem(name);
        SubnetSelector.SetItemMetadata(SubnetSelector.ItemCount - 1, subnet);

        return SubnetSelector.ItemCount - 1;
    }

    private void ClearAllCamerasPoint()
    {
        NavMapScreen.TrackedCoordinates.Clear();
    }
}
