def get_oil_pressure(voltage):
    
    p_slope = 60.482 
    p_intercept = -5.497
    
    return (p_slope * voltage + p_intercept) * 1e5

def get_normal_stress(voltage, fault_type, fault_thickness):
    
    mm = 1e-3
    m = 1
    fault_length = 500 * mm
    
    if fault_type == '1D':
        piston_area = 6.21e-3 * m * m * 3
    if fault_type == '2D':
        piston_area = 6.21e-3 * m * m * 9
        fault_thickness = 500 * mm
    
    pressure = get_oil_pressure(voltage)
    fault_area = fault_thickness * fault_length
    normal_stress = pressure * piston_area / fault_area
    
    return normal_stress # Pa

def get_shear_stress(voltage, fault_type, fault_thickness):
    
    mm = 1e-3
    m = 1
    fault_length = 500 * mm
        
    if fault_type == '1D':
        piston_area = 12.67e-3 * m * m * 1
    if fault_type == '2D':
        piston_area = 12.67e-3 * m * m * 3
        fault_thickness = 500 * mm
    
    pressure = get_oil_pressure(voltage)
    fault_area = fault_thickness * fault_length
    shear_stress = pressure * piston_area / fault_area
    
    return shear_stress # Pa

def get_LVDT_displacement(voltage):
    
    cm = 1e-2
    
    slope     = 0.504
    intercept = 8.946
    
    displacement = (slope * voltage + intercept) * cm
    
    return displacement # unit is m