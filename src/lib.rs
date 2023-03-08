use std::{collections::HashMap, ffi::CStr};

#[repr(C)]
#[derive(Debug)]
pub struct FunctionsMap {
    speak: extern "C" fn(*const std::ffi::c_void, *const i8),
}

#[derive(Debug)]
struct Farm {
    pub animal_ptrs: HashMap<String, *const std::ffi::c_void>,
    pub functions_map: FunctionsMap,
}

impl Farm {
    pub fn add_animal(&mut self, name: String, animal_ptr: *const std::ffi::c_void) {
        self.animal_ptrs.insert(name, animal_ptr);
    }
}

#[no_mangle]
pub extern "C" fn add_animal(
    farm_ptr: *const std::ffi::c_void,
    animal_name: *const i8,
    animal_ptr: *const std::ffi::c_void,
) {
    let farm = farm_ptr as *mut Farm;

    let animal_name_cstr = unsafe { CStr::from_ptr(animal_name) };
    let animal_name = match animal_name_cstr.to_str() {
        Ok(u) => u.to_string(),
        Err(_) => panic!("Couldn't get CStr for animal name in `add_animal`"),
    };

    unsafe { (*farm).add_animal(animal_name, animal_ptr); }
}

#[no_mangle]
pub extern "C" fn get_animal(
    farm_ptr: *const std::ffi::c_void,
    animal_name: *const i8,
) -> *const std::ffi::c_void {
    let farm = farm_ptr as *mut Farm;

    let animal_name_cstr = unsafe { CStr::from_ptr(animal_name) };
    let animal_name = match animal_name_cstr.to_str() {
        Ok(u) => u.to_string(),
        Err(_) => panic!("Couldn't get CStr for animal name in `get_animal`"),
    };

    if let Some(animal_ptr) = unsafe { (*farm).animal_ptrs.get(&animal_name) } {
        *animal_ptr
    } else {
        panic!(
            "Animal with name {} could not be found in farm",
            animal_name
        )
    }
}

#[no_mangle]
pub extern "C" fn create_farm(functions_map: *mut FunctionsMap) -> *const std::ffi::c_void {
    let functions_map = unsafe { *Box::from_raw(functions_map) };

    let farm = Farm {
        animal_ptrs: HashMap::new(),
        functions_map,
    };

    Box::into_raw(Box::new(farm)) as *const std::ffi::c_void
}

#[no_mangle]
pub extern "C" fn native_speak(
    farm_ptr: *const std::ffi::c_void,
    animal_name: *const i8,
    message: *const i8,
) {
    let farm = farm_ptr as *mut Farm;

    let animal_name_cstr = unsafe { CStr::from_ptr(animal_name) };
    let animal_name = match animal_name_cstr.to_str() {
        Ok(u) => u.to_string(),
        Err(_) => panic!("Couldn't get CStr for animal name in `native_speak`"),
    };

    if let Some(animal_ptr) = unsafe { (*farm).animal_ptrs.get(&animal_name) } {
        // TODO: handle not found
        unsafe { ((*farm).functions_map.speak)(*animal_ptr, message) };
    } else {
        panic!(
            "Animal with name {} could not be found in farm",
            animal_name
        )
    }
}
