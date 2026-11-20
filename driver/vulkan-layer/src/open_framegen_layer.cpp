#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include <chrono>
#include <cstring>
#include <mutex>
#include <unordered_map>

#ifdef _WIN32
#define LAYER_EXPORT extern "C" __declspec(dllexport)
#else
#define LAYER_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

struct InstanceDispatch {
    PFN_vkGetInstanceProcAddr next_get_instance_proc_addr = nullptr;
    PFN_vkDestroyInstance destroy_instance = nullptr;
    PFN_vkCreateDevice create_device = nullptr;
};

struct DeviceDispatch {
    PFN_vkGetDeviceProcAddr next_get_device_proc_addr = nullptr;
    PFN_vkDestroyDevice destroy_device = nullptr;
    PFN_vkCreateSwapchainKHR create_swapchain = nullptr;
    PFN_vkQueuePresentKHR queue_present = nullptr;
    uint32_t swapchain_width = 0;
    uint32_t swapchain_height = 0;
    uint64_t real_frame_count = 0;
};

std::mutex g_lock;
std::unordered_map<void*, InstanceDispatch> g_instance_map;
std::unordered_map<void*, DeviceDispatch> g_device_map;

void* DispatchKey(const void* handle) {
    return *reinterpret_cast<void* const*>(handle);
}

const VkLayerInstanceCreateInfo* FindInstanceChainInfo(const VkInstanceCreateInfo* pCreateInfo, VkLayerFunction func) {
    auto* info = reinterpret_cast<const VkLayerInstanceCreateInfo*>(pCreateInfo->pNext);
    while (info) {
        if (info->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && info->function == func) {
            return info;
        }
        info = reinterpret_cast<const VkLayerInstanceCreateInfo*>(info->pNext);
    }
    return nullptr;
}

const VkLayerDeviceCreateInfo* FindDeviceChainInfo(const VkDeviceCreateInfo* pCreateInfo, VkLayerFunction func) {
    auto* info = reinterpret_cast<const VkLayerDeviceCreateInfo*>(pCreateInfo->pNext);
    while (info) {
        if (info->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && info->function == func) {
            return info;
        }
        info = reinterpret_cast<const VkLayerDeviceCreateInfo*>(info->pNext);
    }
    return nullptr;
}

VKAPI_ATTR VkResult VKAPI_CALL Hook_CreateInstance(
    const VkInstanceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkInstance* pInstance)
{
    auto* chainInfo = const_cast<VkLayerInstanceCreateInfo*>(FindInstanceChainInfo(pCreateInfo, VK_LAYER_LINK_INFO));
    if (!chainInfo || !chainInfo->u.pLayerInfo) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    PFN_vkGetInstanceProcAddr fpGetInstanceProcAddr = chainInfo->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    chainInfo->u.pLayerInfo = chainInfo->u.pLayerInfo->pNext;

    auto fpCreateInstance = reinterpret_cast<PFN_vkCreateInstance>(
        fpGetInstanceProcAddr(VK_NULL_HANDLE, "vkCreateInstance"));
    if (!fpCreateInstance) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    VkResult res = fpCreateInstance(pCreateInfo, pAllocator, pInstance);
    if (res != VK_SUCCESS) {
        return res;
    }

    InstanceDispatch disp{};
    disp.next_get_instance_proc_addr = fpGetInstanceProcAddr;
    disp.destroy_instance = reinterpret_cast<PFN_vkDestroyInstance>(
        fpGetInstanceProcAddr(*pInstance, "vkDestroyInstance"));
    disp.create_device = reinterpret_cast<PFN_vkCreateDevice>(
        fpGetInstanceProcAddr(*pInstance, "vkCreateDevice"));

    std::lock_guard<std::mutex> guard(g_lock);
    g_instance_map[DispatchKey(*pInstance)] = disp;
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL Hook_DestroyInstance(
    VkInstance instance,
    const VkAllocationCallbacks* pAllocator)
{
    InstanceDispatch disp{};
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_instance_map.find(DispatchKey(instance));
        if (it != g_instance_map.end()) {
            disp = it->second;
            g_instance_map.erase(it);
        }
    }
    if (disp.destroy_instance) {
        disp.destroy_instance(instance, pAllocator);
    }
}

VKAPI_ATTR VkResult VKAPI_CALL Hook_CreateDevice(
    VkPhysicalDevice physicalDevice,
    const VkDeviceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkDevice* pDevice)
{
    auto* chainInfo = const_cast<VkLayerDeviceCreateInfo*>(FindDeviceChainInfo(pCreateInfo, VK_LAYER_LINK_INFO));
    if (!chainInfo || !chainInfo->u.pLayerInfo) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    PFN_vkGetInstanceProcAddr fpGetInstanceProcAddr = chainInfo->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr fpGetDeviceProcAddr = chainInfo->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    chainInfo->u.pLayerInfo = chainInfo->u.pLayerInfo->pNext;

    auto fpCreateDevice = reinterpret_cast<PFN_vkCreateDevice>(
        fpGetInstanceProcAddr(VK_NULL_HANDLE, "vkCreateDevice"));
    if (!fpCreateDevice) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    VkResult res = fpCreateDevice(physicalDevice, pCreateInfo, pAllocator, pDevice);
    if (res != VK_SUCCESS) {
        return res;
    }

    DeviceDispatch disp{};
    disp.next_get_device_proc_addr = fpGetDeviceProcAddr;
    disp.destroy_device = reinterpret_cast<PFN_vkDestroyDevice>(
        fpGetDeviceProcAddr(*pDevice, "vkDestroyDevice"));
    disp.create_swapchain = reinterpret_cast<PFN_vkCreateSwapchainKHR>(
        fpGetDeviceProcAddr(*pDevice, "vkCreateSwapchainKHR"));
    disp.queue_present = reinterpret_cast<PFN_vkQueuePresentKHR>(
        fpGetDeviceProcAddr(*pDevice, "vkQueuePresentKHR"));

    std::lock_guard<std::mutex> guard(g_lock);
    g_device_map[DispatchKey(*pDevice)] = disp;
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL Hook_DestroyDevice(
    VkDevice device,
    const VkAllocationCallbacks* pAllocator)
{
    DeviceDispatch disp{};
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_device_map.find(DispatchKey(device));
        if (it != g_device_map.end()) {
            disp = it->second;
            g_device_map.erase(it);
        }
    }
    if (disp.destroy_device) {
        disp.destroy_device(device, pAllocator);
    }
}

VKAPI_ATTR VkResult VKAPI_CALL Hook_CreateSwapchainKHR(
    VkDevice device,
    const VkSwapchainCreateInfoKHR* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkSwapchainKHR* pSwapchain)
{
    PFN_vkCreateSwapchainKHR nextCreate = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_device_map.find(DispatchKey(device));
        if (it != g_device_map.end()) {
            it->second.swapchain_width = pCreateInfo->imageExtent.width;
            it->second.swapchain_height = pCreateInfo->imageExtent.height;
            nextCreate = it->second.create_swapchain;
        }
    }
    return nextCreate ? nextCreate(device, pCreateInfo, pAllocator, pSwapchain) : VK_ERROR_INITIALIZATION_FAILED;
}

VKAPI_ATTR VkResult VKAPI_CALL Hook_QueuePresentKHR(
    VkQueue queue,
    const VkPresentInfoKHR* pPresentInfo)
{
    PFN_vkQueuePresentKHR nextPresent = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_device_map.find(DispatchKey(queue));
        if (it != g_device_map.end()) {
            it->second.real_frame_count++;
            nextPresent = it->second.queue_present;
        }
    }
    return nextPresent ? nextPresent(queue, pPresentInfo) : VK_ERROR_DEVICE_LOST;
}

} // namespace

LAYER_EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL OpenFrameGen_GetDeviceProcAddr(VkDevice device, const char* pName) {
    if (std::strcmp(pName, "vkGetDeviceProcAddr") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(OpenFrameGen_GetDeviceProcAddr);
    if (std::strcmp(pName, "vkDestroyDevice") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_DestroyDevice);
    if (std::strcmp(pName, "vkCreateSwapchainKHR") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_CreateSwapchainKHR);
    if (std::strcmp(pName, "vkQueuePresentKHR") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_QueuePresentKHR);

    std::lock_guard<std::mutex> guard(g_lock);
    auto it = g_device_map.find(DispatchKey(device));
    if (it != g_device_map.end() && it->second.next_get_device_proc_addr) {
        return it->second.next_get_device_proc_addr(device, pName);
    }
    return nullptr;
}

LAYER_EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL OpenFrameGen_GetInstanceProcAddr(VkInstance instance, const char* pName) {
    if (std::strcmp(pName, "vkGetInstanceProcAddr") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(OpenFrameGen_GetInstanceProcAddr);
    if (std::strcmp(pName, "vkCreateInstance") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_CreateInstance);
    if (std::strcmp(pName, "vkDestroyInstance") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_DestroyInstance);
    if (std::strcmp(pName, "vkCreateDevice") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(Hook_CreateDevice);
    if (std::strcmp(pName, "vkGetDeviceProcAddr") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(OpenFrameGen_GetDeviceProcAddr);

    std::lock_guard<std::mutex> guard(g_lock);
    auto it = g_instance_map.find(DispatchKey(instance));
    if (it != g_instance_map.end() && it->second.next_get_instance_proc_addr) {
        return it->second.next_get_instance_proc_addr(instance, pName);
    }
    return nullptr;
}

LAYER_EXPORT VKAPI_ATTR VkResult VKAPI_CALL OpenFrameGen_NegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* pVersionStruct) {
    if (!pVersionStruct || pVersionStruct->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (pVersionStruct->loaderLayerInterfaceVersion >= 2) {
        pVersionStruct->loaderLayerInterfaceVersion = 2;
        pVersionStruct->pfnGetInstanceProcAddr = OpenFrameGen_GetInstanceProcAddr;
        pVersionStruct->pfnGetDeviceProcAddr = OpenFrameGen_GetDeviceProcAddr;
        pVersionStruct->pfnGetPhysicalDeviceProcAddr = nullptr;
    }
    return VK_SUCCESS;
}
