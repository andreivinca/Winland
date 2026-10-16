using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Winland.Env;

/// <summary>
/// The access-control list both of the environment's pipes are created with. The default DACL of a
/// pipe created by this elevated service rejects the user's own unelevated processes — which would
/// force every winlandctl caller (helper scripts, terminals) to run elevated, and would leave the
/// unelevated indicator unable to subscribe at all. Granting the logged-on user's SID full control
/// admits them. The trade: any same-user process may drive the window-management verbs and read the
/// workspace state; both surfaces are deliberately limited to that.
/// </summary>
internal static class PipeAcl
{
    public static PipeSecurity ForCurrentUser()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }
}
