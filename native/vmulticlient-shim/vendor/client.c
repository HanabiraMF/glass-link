#include <windows.h>
#include <hidsdi.h>
#include <setupapi.h>
#include <stdio.h>
#include <stdlib.h>

#include "vmulticlient.h"

#if __GNUC__
    #define __in
    #define __in_ecount(x)
    typedef void* PVOID;
    typedef PVOID HDEVINFO;
    WINHIDSDI BOOL WINAPI HidD_SetOutputReport(HANDLE, PVOID, ULONG);
#endif

typedef struct _vmulti_client_t
{
    HANDLE hControl;
    HANDLE hMessage;
    BYTE controlReport[CONTROL_REPORT_SIZE];
} vmulti_client_t;

//
// Function prototypes
//

HANDLE
SearchMatchingHwID (
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage
    );

HANDLE
OpenDeviceInterface (
    _In_ HDEVINFO HardwareDeviceInfo,
    _In_ PSP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage
    );

BOOLEAN
CheckIfOurDevice(
    _In_ HANDLE file,
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage
    );

BOOL
HidOutput(
    _In_ BOOL useSetOutputReport,
    _In_ HANDLE file,
    _In_reads_bytes_(bufferSize) PCHAR buffer,
    _In_ ULONG bufferSize
    );

//
// Copied this structure from hidport.h
//

typedef struct _HID_DEVICE_ATTRIBUTES {

    ULONG           Size;
    //
    // sizeof (struct _HID_DEVICE_ATTRIBUTES)
    //
    //
    // Vendor ids of this hid device
    //
    USHORT          VendorID;
    USHORT          ProductID;
    USHORT          VersionNumber;
    USHORT          Reserved[11];

} HID_DEVICE_ATTRIBUTES, * PHID_DEVICE_ATTRIBUTES;

//
// Implementation
//

_Check_return_
pvmulti_client vmulti_alloc(void)
{
    return (pvmulti_client)malloc(sizeof(vmulti_client_t));
}

void vmulti_free(_In_ _Post_invalid_ pvmulti_client vmulti)
{
    free(vmulti);
}

_Check_return_
BOOL vmulti_connect(_Inout_ pvmulti_client vmulti)
{
    // Set thread priority higher to improve performance and reduce timing issues
    // that might occur under Windows 11
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    
    //
    // Find the HID devices
    // Enhanced for Windows 11 with better error diagnostics
    //

    vmulti->hControl = SearchMatchingHwID(0xff00, 0x0001);
    if (vmulti->hControl == INVALID_HANDLE_VALUE || vmulti->hControl == NULL) {
        printf("Failed to connect to control interface (0xff00, 0x0001) - Error: %d\n", GetLastError());
        return FALSE;
    }
    
    vmulti->hMessage = SearchMatchingHwID(0xff00, 0x0002);
    if (vmulti->hMessage == INVALID_HANDLE_VALUE || vmulti->hMessage == NULL) {
        printf("Failed to connect to message interface (0xff00, 0x0002) - Error: %d\n", GetLastError());
        vmulti_disconnect(vmulti);
        return FALSE;
    }

    //
    // Set the buffer count to 10 on the message HID
    //

    if (!HidD_SetNumInputBuffers(vmulti->hMessage, 10))
    {
        printf("failed HidD_SetNumInputBuffers %d\n", GetLastError());
        vmulti_disconnect(vmulti);
        return FALSE;
    }

    return TRUE;
}

void vmulti_disconnect(_Inout_ pvmulti_client vmulti)
{
    if (vmulti->hControl != NULL)
        CloseHandle(vmulti->hControl);
    if (vmulti->hMessage != NULL)
        CloseHandle(vmulti->hMessage);
    vmulti->hControl = NULL;
    vmulti->hMessage = NULL;
}

_Check_return_
BOOL vmulti_update_mouse(
    _In_ pvmulti_client vmulti, 
    _In_ BYTE button, 
    _In_ USHORT x, 
    _In_ USHORT y, 
    _In_ BYTE wheelPosition)
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiMouseReport* pMouseReport = NULL;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiMouseReport))
    {
        return FALSE;
    }

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiMouseReport);

    //
    // Set the input report
    //

    pMouseReport = (VMultiMouseReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
    pMouseReport->ReportID = REPORTID_MOUSE;
    pMouseReport->Button = button;
    pMouseReport->XValue = x;
    pMouseReport->YValue = y;
    pMouseReport->WheelPosition = wheelPosition;

    // Send the report
    return HidOutput(FALSE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE);
}

_Check_return_
BOOL vmulti_update_relative_mouse(
    _In_ pvmulti_client vmulti, 
    _In_ BYTE button,
    _In_ BYTE x, 
    _In_ BYTE y, 
    _In_ BYTE wheelPosition)
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiRelativeMouseReport* pMouseReport = NULL;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiRelativeMouseReport))
    {
        return FALSE;
    }

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiRelativeMouseReport);

    //
    // Set the input report
    //

    pMouseReport = (VMultiRelativeMouseReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
    pMouseReport->ReportID = REPORTID_RELATIVE_MOUSE;
    pMouseReport->Button = button;
    pMouseReport->XValue = x;
    pMouseReport->YValue = y;
    pMouseReport->WheelPosition = wheelPosition;

    // Send the report
    return HidOutput(FALSE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE);
}

_Check_return_
BOOL vmulti_update_digi(
    _In_ pvmulti_client vmulti, 
    _In_ BYTE status, 
    _In_ USHORT x, 
    _In_ USHORT y)
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiDigiReport* pDigiReport = NULL;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiDigiReport))
    {
        return FALSE;
    }

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiDigiReport);

    //
    // Set the input report
    //

    pDigiReport = (VMultiDigiReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
    pDigiReport->ReportID = REPORTID_DIGI;
    pDigiReport->Status = status;
    pDigiReport->XValue = x;
    pDigiReport->YValue = y;

    // Send the report
    return HidOutput(FALSE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE);
}

_Check_return_
BOOL vmulti_update_multitouch(
    _In_ pvmulti_client vmulti, 
    _In_reads_(actualCount) PTOUCH pTouch, 
    _In_ BYTE actualCount)
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiMultiTouchReport* pMultiReport = NULL;
    int numberOfTouchesSent = 0;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiMultiTouchReport))
        return FALSE;

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiMultiTouchReport);

    while (numberOfTouchesSent < actualCount)
    {

        //
        // Set the input report
        //

        pMultiReport = (VMultiMultiTouchReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
        pMultiReport->ReportID = REPORTID_MTOUCH;
        memcpy(pMultiReport->Touch, pTouch + numberOfTouchesSent, sizeof(TOUCH));
        if (numberOfTouchesSent <= actualCount - 2)
            memcpy(pMultiReport->Touch + 1, pTouch + numberOfTouchesSent + 1, sizeof(TOUCH));
        else
            memset(pMultiReport->Touch + 1, 0, sizeof(TOUCH));
        if (numberOfTouchesSent == 0)
            pMultiReport->ActualCount = actualCount;
        else
            pMultiReport->ActualCount = 0;

        // Send the report
        if (!HidOutput(TRUE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE))
            return FALSE;

        numberOfTouchesSent += 2;
    }

    return TRUE;
}

_Check_return_
BOOL vmulti_update_joystick(
    _In_ pvmulti_client vmulti, 
    _In_ USHORT buttons, 
    _In_ BYTE hat, 
    _In_ BYTE x, 
    _In_ BYTE y, 
    _In_ BYTE rx, 
    _In_ BYTE ry, 
    _In_ BYTE throttle)
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiJoystickReport* pJoystickReport = NULL;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiJoystickReport))
    {
        return FALSE;
    }

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiJoystickReport);

    //
    // Set the input report
    //

    pJoystickReport = (VMultiJoystickReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
    pJoystickReport->ReportID = REPORTID_JOYSTICK;
    pJoystickReport->Buttons = buttons;
    pJoystickReport->Hat = hat;
    pJoystickReport->XValue = x;
    pJoystickReport->YValue = y;
    pJoystickReport->RXValue = rx;
    pJoystickReport->RYValue = ry;
    pJoystickReport->Throttle = throttle;

    // Send the report
    return HidOutput(FALSE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE);
}

_Check_return_
BOOL vmulti_update_keyboard(
    _In_ pvmulti_client vmulti, 
    _In_ BYTE shiftKeyFlags, 
    _In_reads_(KBD_KEY_CODES) BYTE keyCodes[KBD_KEY_CODES])
{
    VMultiControlReportHeader* pReport = NULL;
    VMultiKeyboardReport* pKeyboardReport = NULL;

    if (CONTROL_REPORT_SIZE <= sizeof(VMultiControlReportHeader) + sizeof(VMultiKeyboardReport))
    {
        return FALSE;
    }

    //
    // Set the report header
    //

    pReport = (VMultiControlReportHeader*)vmulti->controlReport;
    pReport->ReportID = REPORTID_CONTROL;
    pReport->ReportLength = sizeof(VMultiKeyboardReport);

    //
    // Set the input report
    //

    pKeyboardReport = (VMultiKeyboardReport*)(vmulti->controlReport + sizeof(VMultiControlReportHeader));
    pKeyboardReport->ReportID = REPORTID_KEYBOARD;
    pKeyboardReport->ShiftKeyFlags = shiftKeyFlags;
    memcpy(pKeyboardReport->KeyCodes, keyCodes, KBD_KEY_CODES);

    // Send the report
    return HidOutput(FALSE, vmulti->hControl, (PCHAR)vmulti->controlReport, CONTROL_REPORT_SIZE);
}

_Check_return_
BOOL vmulti_write_message(
    _In_ pvmulti_client vmulti, 
    _In_ VMultiMessageReport* pReport)
{
    VMultiControlReportHeader* pReportHeader;
    ULONG bytesWritten;

    //
    // Set the report header
    //

    pReportHeader = (VMultiControlReportHeader*)vmulti->controlReport;
    pReportHeader->ReportID = REPORTID_CONTROL;
    pReportHeader->ReportLength = sizeof(VMultiMessageReport);

    //
    // Set the body
    //

    pReport->ReportID = REPORTID_MESSAGE;
    memcpy(vmulti->controlReport + sizeof(VMultiControlReportHeader), pReport, sizeof(VMultiMessageReport));

    //
    // Write the report
    //

    if (!WriteFile(vmulti->hControl, vmulti->controlReport, CONTROL_REPORT_SIZE, &bytesWritten, NULL))
    {
        printf("failed WriteFile %d\n", GetLastError());
        return FALSE;
    }

    return TRUE;
}

_Check_return_
BOOL vmulti_read_message(
    _In_ pvmulti_client vmulti, 
    _Out_ VMultiMessageReport* pReport)
{
    ULONG bytesRead;

    //
    // Read the report
    //

    if (!ReadFile(vmulti->hMessage, pReport, sizeof(VMultiMessageReport), &bytesRead, NULL))
    {
        printf("failed ReadFile %d\n", GetLastError());
        return FALSE;
    }

    return TRUE;
}

HANDLE
SearchMatchingHwID (
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage
    )
{
    HDEVINFO                  hardwareDeviceInfo;
    SP_DEVICE_INTERFACE_DATA  deviceInterfaceData;
    SP_DEVINFO_DATA           devInfoData;
    GUID                      hidguid;
    int                       i;

    HidD_GetHidGuid(&hidguid);

    // Updated for Windows 11 - more robust device discovery
    hardwareDeviceInfo =
            SetupDiGetClassDevs ((LPGUID)&hidguid,
                                            NULL,
                                            NULL,
                                            (DIGCF_PRESENT |
                                            DIGCF_DEVICEINTERFACE));

    if (INVALID_HANDLE_VALUE == hardwareDeviceInfo)
    {
        printf("SetupDiGetClassDevs failed: %x\n", GetLastError());
        return INVALID_HANDLE_VALUE;
    }

    deviceInterfaceData.cbSize = sizeof (SP_DEVICE_INTERFACE_DATA);

    devInfoData.cbSize = sizeof(SP_DEVINFO_DATA);

    //
    // Enumerate devices of this interface class
    //

    printf("\n....looking for our HID device (with UP=0x%x "
                "and Usage=0x%x)\n", myUsagePage, myUsage);

    for (i = 0; SetupDiEnumDeviceInterfaces (hardwareDeviceInfo,
                            0, // No care about specific PDOs
                            (LPGUID)&hidguid,
                            i, //
                            &deviceInterfaceData);
                            i++)
    {

        //
        // Open the device interface and Check if it is our device
        // by matching the Usage page and Usage from Hid_Caps.
        // If this is our device then send the hid request.
        //

        HANDLE file = OpenDeviceInterface(hardwareDeviceInfo, &deviceInterfaceData, myUsagePage, myUsage);

        if (file != INVALID_HANDLE_VALUE)
        {
            SetupDiDestroyDeviceInfoList (hardwareDeviceInfo);
            return file;
        }

        //
        //device was not found so loop around.
        //

    }

    // More detailed logging for Windows 11 troubleshooting
    DWORD error = GetLastError();
    printf("Failure: Could not find our HID device with UsagePage=0x%x, Usage=0x%x\n", myUsagePage, myUsage);
    printf("Last error code: %d (0x%x)\n", error, error);

    SetupDiDestroyDeviceInfoList (hardwareDeviceInfo);

    return INVALID_HANDLE_VALUE;
}

HANDLE
OpenDeviceInterface (
    _In_ HDEVINFO hardwareDeviceInfo,
    _In_ PSP_DEVICE_INTERFACE_DATA deviceInterfaceData,
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage
    )
{
    PSP_DEVICE_INTERFACE_DETAIL_DATA    deviceInterfaceDetailData = NULL;

    DWORD        predictedLength = 0;
    DWORD        requiredLength = 0;
    HANDLE       file = INVALID_HANDLE_VALUE;

    SetupDiGetDeviceInterfaceDetail(
                            hardwareDeviceInfo,
                            deviceInterfaceData,
                            NULL, // probing so no output buffer yet
                            0, // probing so output buffer length of zero
                            &requiredLength,
                            NULL
                            ); // not interested in the specific dev-node

    predictedLength = requiredLength;

    deviceInterfaceDetailData =
         (PSP_DEVICE_INTERFACE_DETAIL_DATA) malloc (predictedLength);

    if (!deviceInterfaceDetailData)
    {
        printf("Error: OpenDeviceInterface: malloc failed\n");
        goto cleanup;
    }

    deviceInterfaceDetailData->cbSize =
                    sizeof (SP_DEVICE_INTERFACE_DETAIL_DATA);

    if (!SetupDiGetDeviceInterfaceDetail(
                            hardwareDeviceInfo,
                            deviceInterfaceData,
                            deviceInterfaceDetailData,
                            predictedLength,
                            &requiredLength,
                            NULL))
    {
        printf("Error: SetupDiGetInterfaceDeviceDetail failed\n");
        free (deviceInterfaceDetailData);
        goto cleanup;
    }

    // Updated for Windows 11 with proper sharing mode (both read and write)
    // Using FILE_ATTRIBUTE_NORMAL instead of FILE_FLAG_OVERLAPPED to avoid
    // having to implement full overlapped I/O
    file = CreateFile ( deviceInterfaceDetailData->DevicePath,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            NULL, // no SECURITY_ATTRIBUTES structure
                            OPEN_EXISTING, // No special create flags
                            FILE_ATTRIBUTE_NORMAL, // Standard synchronous I/O
                            NULL); // No template file

    if (INVALID_HANDLE_VALUE == file) {
        printf("Error: CreateFile failed: %d\n", GetLastError());
        goto cleanup;
    }

    // Fixed logic: if we found our device, return the file handle directly
    // without going to cleanup which would close the handle
    if (CheckIfOurDevice(file, myUsagePage, myUsage)) {
        // Success - return the file handle
        free(deviceInterfaceDetailData);
        return file;
    }

    CloseHandle(file);

    file = INVALID_HANDLE_VALUE;

cleanup:

    free (deviceInterfaceDetailData);

    return file;

}


BOOLEAN
CheckIfOurDevice(
    _In_ HANDLE file,
    _In_ USAGE myUsagePage,
    _In_ USAGE myUsage)
{
    PHIDP_PREPARSED_DATA Ppd = NULL; // The opaque parser info describing this device
    HIDD_ATTRIBUTES                 Attributes; // The Attributes of this hid device.
    HIDP_CAPS                       Caps; // The Capabilities of this hid device.
    BOOLEAN                         result = FALSE;

    if (!HidD_GetPreparsedData (file, &Ppd))
    {
        printf("Error: HidD_GetPreparsedData failed \n");
        goto cleanup;
    }

    if (!HidD_GetAttributes(file, &Attributes))
    {
        printf("Error: HidD_GetAttributes failed \n");
        goto cleanup;
    }

    if (Attributes.VendorID == VMULTI_VID && Attributes.ProductID == VMULTI_PID)
    {
        if (!HidP_GetCaps (Ppd, &Caps))
        {
            printf("Error: HidP_GetCaps failed \n");
            goto cleanup;
        }

        if ((Caps.UsagePage == myUsagePage) && (Caps.Usage == myUsage))
        {
            printf("Success: Found my device.. \n");
            result = TRUE;
        }
    }

cleanup:

    if (Ppd != NULL)
    {
        HidD_FreePreparsedData (Ppd);
    }

    return result;
}

BOOL
HidOutput(
    _In_ BOOL useSetOutputReport,
    _In_ HANDLE file,
    _In_reads_bytes_(bufferSize) PCHAR buffer,
    _In_ ULONG bufferSize
    )
{
    ULONG bytesWritten;
    if (useSetOutputReport)
    {
        //
        // Send Hid report thru HidD_SetOutputReport API
        //

        if (!HidD_SetOutputReport(file, buffer, bufferSize))
        {
            printf("failed HidD_SetOutputReport %d\n", GetLastError());
            return FALSE;
        }
    }
    else
    {
        // Since we've updated CreateFile to use FILE_ATTRIBUTE_NORMAL, we can
        // call WriteFile directly without needing to reopen the file
        if (!WriteFile(file, buffer, bufferSize, &bytesWritten, NULL))
        {
            DWORD error = GetLastError();
            printf("failed WriteFile %d (0x%x)\n", error, error);
            return FALSE;
        }
    }

    return TRUE;
}